using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Slice 2 of #122. In-process tracking of in-flight cancel-replace
/// requests, keyed by the new ClOrdID. The endpoint (slice 4) records
/// an intent here BEFORE dispatching to the gateway; the
/// <see cref="ExecutionReportProcessor"/> consumes it on the matching
/// Replaced ER (success path) or Rejected ER (replace-reject path).
///
/// <para>
/// Slice 2 is in-memory only. Slice 4 will append a matching
/// <c>OrderReplaceRequestedEvent</c> to the WAL so the registry can be
/// rebuilt on cold start without losing in-flight modifies.
/// </para>
///
/// <para>
/// Concurrency: <see cref="ConcurrentDictionary{TKey,TValue}"/> backs
/// the storage; <see cref="TryAdd"/> / <see cref="TryConsume"/> are
/// thread-safe atomic operations. Slice 4 will additionally enforce
/// "one in-flight modify per original ClOrdID" by indexing on
/// <see cref="OrderReplacementIntent.OriginalClOrdId"/>.
/// </para>
/// </summary>
public sealed class PendingReplacementRegistry
{
    // Internal entry wrapper: carries the public intent plus
    // bookkeeping metadata needed by the pass-4 (#299) P1 ambiguous-
    // send margin convergence path (see SweepExpiredAmbiguous +
    // MarkAmbiguousMarginHeld below). The public OrderReplacementIntent
    // record is intentionally NOT extended — its shape is captured
    // verbatim into OrderReplaceRequestedEvent for WAL replay and
    // existing tests construct it positionally; widening the record
    // would ripple unnecessarily.
    private sealed class Entry
    {
        public OrderReplacementIntent Intent { get; }
        public DateTimeOffset CreatedAt { get; }
        // Pass-4 review (#299) P1. Set when the gateway dispatch
        // threw AFTER the margin coordinator's PrepareReplace
        // succeeded AND the intent was registered. The reservation
        // under <see cref="OrderReplacementIntent.NewClOrdId"/> is
        // left in place (rather than aborted) so a late Replaced ER
        // can converge through CommitReplace without re-checking
        // capacity — but it must be released by the sweep if no ER
        // arrives within the configured TTL, otherwise the upsize
        // delta leaks until the parent terminates.
        public bool AmbiguousMarginHeld { get; set; }
        public Entry(OrderReplacementIntent intent, DateTimeOffset createdAt)
        {
            Intent = intent;
            CreatedAt = createdAt;
        }
    }

    private readonly ConcurrentDictionary<ulong, Entry> _byNewClOrdId = new();
    // Secondary index: original ClOrdID → new ClOrdID. Enforces the
    // "one in-flight modify per original" guard (slice 4).
    private readonly ConcurrentDictionary<ulong, ulong> _byOriginalClOrdId = new();

    /// <summary>
    /// Records an in-flight modify. Returns <c>false</c> when an intent
    /// for the same <paramref name="intent"/>.<see cref="OrderReplacementIntent.NewClOrdId"/>
    /// is already tracked OR when there's already an in-flight modify
    /// for the same <see cref="OrderReplacementIntent.OriginalClOrdId"/>
    /// (the slice-4 guard) — exactly one pending modify per original
    /// order at any time.
    /// </summary>
    public bool TryAdd(OrderReplacementIntent intent) => TryAdd(intent, default);

    /// <summary>
    /// Pass-4 review (#299) P1 overload. Same as <see cref="TryAdd(OrderReplacementIntent)"/>
    /// but stamps <paramref name="createdAt"/> on the entry so the
    /// ambiguous-margin sweep (<see cref="SweepExpiredAmbiguous"/>)
    /// can release reservations whose intent has been pending too
    /// long without a Replaced/Rejected ER.
    /// </summary>
    public bool TryAdd(OrderReplacementIntent intent, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        // Reserve the orig slot first; if successful, claim the new
        // slot. If new slot is unexpectedly taken (collision on the
        // ClOrdID counter — should never happen in practice), roll
        // back the orig reservation.
        if (!_byOriginalClOrdId.TryAdd(intent.OriginalClOrdId, intent.NewClOrdId))
            return false;
        if (!_byNewClOrdId.TryAdd(intent.NewClOrdId, new Entry(intent, createdAt)))
        {
            _byOriginalClOrdId.TryRemove(intent.OriginalClOrdId, out _);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Removes and returns the intent for <paramref name="newClOrdId"/>
    /// if present. Used by the ER processor on a successful Replaced
    /// ack or a replace-reject — both terminate the in-flight state.
    /// </summary>
    public bool TryConsume(ulong newClOrdId, out OrderReplacementIntent? intent)
    {
        if (_byNewClOrdId.TryRemove(newClOrdId, out var found))
        {
            _byOriginalClOrdId.TryRemove(found.Intent.OriginalClOrdId, out _);
            intent = found.Intent;
            return true;
        }
        intent = null;
        return false;
    }

    /// <summary>
    /// Pass-4 review (#299) P1. Consume the pending intent (if any)
    /// keyed by <paramref name="originalClOrdId"/>. Returns the
    /// intent on success along with <paramref name="ambiguousMarginHeld"/>
    /// so the caller (ER processor on a Canceled ER for the orig)
    /// can release the still-held margin reservation when the venue
    /// effectively dropped the cancel-replace.
    /// </summary>
    public bool TryConsumeByOriginal(
        ulong originalClOrdId,
        out OrderReplacementIntent? intent,
        out bool ambiguousMarginHeld)
    {
        if (_byOriginalClOrdId.TryRemove(originalClOrdId, out var newId)
            && _byNewClOrdId.TryRemove(newId, out var found))
        {
            intent = found.Intent;
            ambiguousMarginHeld = found.AmbiguousMarginHeld;
            return true;
        }
        intent = null;
        ambiguousMarginHeld = false;
        return false;
    }

    /// <summary>
    /// Peeks at the intent without removing it — used by callers that
    /// need to distinguish a normal Rejected ER from a replace-reject
    /// before deciding to consume.
    /// </summary>
    public bool TryGet(ulong newClOrdId, out OrderReplacementIntent? intent)
    {
        if (_byNewClOrdId.TryGetValue(newClOrdId, out var found))
        {
            intent = found.Intent;
            return true;
        }
        intent = null;
        return false;
    }

    /// <summary>
    /// Slice 4 of #122: in-flight guard for the modify endpoint.
    /// Returns <c>true</c> when there's already a pending modify for
    /// <paramref name="originalClOrdId"/> — the endpoint rejects with
    /// 409 in that case so the caller cannot stack two modify requests
    /// on the same order and race the venue.
    /// </summary>
    public bool IsOriginalInFlight(ulong originalClOrdId) =>
        _byOriginalClOrdId.ContainsKey(originalClOrdId);

    /// <summary>
    /// Pass-4 review (#299) P1. Mark the entry under
    /// <paramref name="newClOrdId"/> as having a still-held margin
    /// reservation. Called by the AlgoEngine modify path AFTER an
    /// ambiguous gateway dispatch failure — the intent is kept in
    /// place so a late Replaced ER can converge, but the reservation
    /// will leak indefinitely if no ER ever arrives. The sweep below
    /// uses this flag to bound the leak via TTL. Returns <c>false</c>
    /// when no entry exists (e.g. the intent was already consumed
    /// by a racing ER between dispatch failure and this call).
    /// </summary>
    public bool MarkAmbiguousMarginHeld(ulong newClOrdId)
    {
        if (_byNewClOrdId.TryGetValue(newClOrdId, out var found))
        {
            found.AmbiguousMarginHeld = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Pass-4 review (#299) P1. Remove + return every entry whose
    /// <see cref="Entry.AmbiguousMarginHeld"/> flag is set AND whose
    /// <see cref="Entry.CreatedAt"/> is older than
    /// <paramref name="now"/> minus <paramref name="ttl"/>. Caller
    /// (the AlgoScheduler sweep) is responsible for calling
    /// <see cref="Risk.IReplaceMarginCoordinator.AbortReplace"/> for
    /// each returned intent and bumping the expired-counter metric.
    /// Entries without the ambiguous flag (the normal in-flight state)
    /// are NEVER reaped — a long-lived modify on a slow venue is
    /// legitimate.
    /// </summary>
    public IReadOnlyList<OrderReplacementIntent> SweepExpiredAmbiguous(
        DateTimeOffset now, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero) return Array.Empty<OrderReplacementIntent>();
        List<OrderReplacementIntent>? expired = null;
        var cutoff = now - ttl;
        foreach (var kvp in _byNewClOrdId)
        {
            var entry = kvp.Value;
            if (!entry.AmbiguousMarginHeld) continue;
            if (entry.CreatedAt > cutoff) continue;
            // Atomic remove guarded by the secondary index so a
            // racing TryConsume(newClOrdId) wins cleanly (only one
            // side will observe the entry as removable).
            if (_byNewClOrdId.TryRemove(kvp.Key, out var found))
            {
                _byOriginalClOrdId.TryRemove(found.Intent.OriginalClOrdId, out _);
                (expired ??= new List<OrderReplacementIntent>()).Add(found.Intent);
            }
        }
        return (IReadOnlyList<OrderReplacementIntent>?)expired ?? Array.Empty<OrderReplacementIntent>();
    }

    /// <summary>Test/observability helper.</summary>
    internal int CountForTesting => _byNewClOrdId.Count;
}

/// <summary>
/// Captures everything the <see cref="ExecutionReportProcessor"/>
/// needs to hydrate a replacement <see cref="Order"/> when the
/// matching Replaced ER arrives. All fields except cum/leaves come
/// from the modify request; cum/leaves are filled in from the ER.
/// </summary>
public sealed record OrderReplacementIntent(
    ulong OriginalClOrdId,
    ulong NewClOrdId,
    EndClientId Owner,
    string Symbol,
    ulong SecurityId,
    OrderSide Side,
    OrderType Type,
    long NewQuantity,
    decimal? NewPrice,
    string FirmId,
    ulong? ParentAlgoId,
    int? AlgoSliceSeq,
    // Q1.1 (#253). Optional modify-pipeline overrides. Null = inherit
    // the original Order's value at HydrateReplacement time; non-null
    // = override. CommitReplace passes these through Order.HydrateReplacement
    // so the replacement Order carries either the new requested value
    // or the original, as decided by the modify request.
    TimeInForce? RequestedTimeInForce = null,
    decimal? RequestedStopPrice = null,
    DateTimeOffset? RequestedGoodTillDate = null);
