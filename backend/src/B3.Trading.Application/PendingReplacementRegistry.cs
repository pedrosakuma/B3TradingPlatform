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
    private readonly ConcurrentDictionary<ulong, OrderReplacementIntent> _byNewClOrdId = new();
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
    public bool TryAdd(OrderReplacementIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        // Reserve the orig slot first; if successful, claim the new
        // slot. If new slot is unexpectedly taken (collision on the
        // ClOrdID counter — should never happen in practice), roll
        // back the orig reservation.
        if (!_byOriginalClOrdId.TryAdd(intent.OriginalClOrdId, intent.NewClOrdId))
            return false;
        if (!_byNewClOrdId.TryAdd(intent.NewClOrdId, intent))
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
            _byOriginalClOrdId.TryRemove(found.OriginalClOrdId, out _);
            intent = found;
            return true;
        }
        intent = null;
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
            intent = found;
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
    int? AlgoSliceSeq);
