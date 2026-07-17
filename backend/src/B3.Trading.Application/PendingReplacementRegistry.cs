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
        // Pass-5 review (#299) P1. Wall-clock at which the entry was
        // marked ambiguous. Used by the TTL sweep as the age anchor
        // (vs. CreatedAt which lags from intent registration); also
        // persisted in <c>OrderReplaceAmbiguousMarginHeldEvent</c>
        // so post-restart replay rebuilds the same TTL deadline the
        // pre-crash sweep would have observed.
        public DateTimeOffset? AmbiguousAt { get; set; }
        // Pass-6 review (#299) P1. Captured from the AlgoEngine modify
        // path when the entry is flagged ambiguous; persisted in the
        // <c>OrderReplaceAmbiguousMarginHeldEvent</c> AND in
        // <c>RawPlatformSnapshot.PendingReplacements</c> so post-restart
        // recovery — whether driven by WAL tail or by a snapshot whose
        // Seq is past the ambiguous mark — can re-invoke
        // <see cref="Risk.IReplaceMarginCoordinator.PrepareReplaceAsync"/>
        // with the same value the pre-crash dispatch used.
        public decimal NewRemainingNotional { get; set; }
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
    // Pass-5 review (#299) P2. Tertiary index: new ClOrdIDs whose
    // entry is flagged <see cref="Entry.AmbiguousMarginHeld"/>. The
    // TTL sweep enumerates THIS set rather than the full registry so
    // its cost is O(ambiguous) — typically zero — instead of O(all
    // pending modifies). Membership is maintained in lock-step with
    // <see cref="MarkAmbiguousMarginHeld(ulong, DateTimeOffset)"/>
    // (add) and every consume/expire site (remove). The set uses
    // <c>byte</c> as the value type because <c>ConcurrentDictionary</c>
    // has no <c>ConcurrentHashSet</c> equivalent in BCL; the value
    // is never read.
    private readonly ConcurrentDictionary<ulong, byte> _ambiguous = new();
    // Pass-5 review (#299) P2 test hook. Counts the number of
    // registry entries the most recent <see cref="SweepExpiredAmbiguous"/>
    // call inspected. Exposed via the internal helper below so the
    // unit test can assert the sweep enumerates O(ambiguous), not
    // O(all). Reset at the top of every sweep.
    private long _lastSweepInspectedCount;

    /// <summary>
    /// Atomically claims an original order before the modify pipeline allocates
    /// a ClOrdID or runs risk/margin work. The zero value is an internal
    /// transient marker only; platform ClOrdIDs can never be zero.
    /// </summary>
    public bool TryClaimOriginal(ulong originalClOrdId)
    {
        if (originalClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(originalClOrdId));
        return _byOriginalClOrdId.TryAdd(originalClOrdId, 0);
    }

    /// <summary>
    /// Releases a transient claim that never became a WAL-backed replacement.
    /// Does not remove a registered in-flight intent.
    /// </summary>
    public bool ReleaseOriginalClaim(ulong originalClOrdId) =>
        ((ICollection<KeyValuePair<ulong, ulong>>)_byOriginalClOrdId)
            .Remove(new KeyValuePair<ulong, ulong>(originalClOrdId, 0));

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
    /// Converts a transient original-order claim into a durable in-flight
    /// replacement. Called only from the OrderReplaceRequestedEvent apply
    /// callback, after the WAL append has succeeded.
    /// </summary>
    public bool TryAddClaimed(OrderReplacementIntent intent, DateTimeOffset createdAt = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var entry = new Entry(intent, createdAt);
        if (!_byNewClOrdId.TryAdd(intent.NewClOrdId, entry))
            return false;
        if (_byOriginalClOrdId.TryUpdate(intent.OriginalClOrdId, intent.NewClOrdId, 0))
            return true;
        _byNewClOrdId.TryRemove(intent.NewClOrdId, out _);
        return false;
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
            _ambiguous.TryRemove(newClOrdId, out _);
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
            _ambiguous.TryRemove(newId, out _);
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

    public bool IsAmbiguous(ulong newClOrdId) =>
        _byNewClOrdId.TryGetValue(newClOrdId, out var entry)
        && entry.AmbiguousMarginHeld;

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
    /// <para>
    /// Pass-5 review (#299) P1. The <paramref name="ambiguousAt"/>
    /// timestamp is captured on the entry AND persisted via the
    /// matching <c>OrderReplaceAmbiguousMarginHeldEvent</c> so a
    /// post-restart replay re-hydrates the same TTL deadline the
    /// pre-crash sweep would have observed. Pass the engine clock's
    /// current value; the sweep below ages from this stamp, not
    /// from <see cref="Entry.CreatedAt"/>.
    /// </para>
    /// </summary>
    public bool MarkAmbiguousMarginHeld(ulong newClOrdId, DateTimeOffset ambiguousAt)
        => MarkAmbiguousMarginHeld(newClOrdId, ambiguousAt, newRemainingNotional: 0m);

    /// <summary>
    /// Pass-6 review (#299) P1 overload. Captures the upsize-delta-
    /// bearing new remaining notional alongside the ambiguous flag so
    /// a snapshot taken after the ambiguous mark can be projected into
    /// <c>RawPlatformSnapshot.PendingReplacements</c> AND a restore can
    /// re-invoke <c>PrepareReplaceAsync</c> with the same value the
    /// pre-crash dispatch used.
    /// </summary>
    public bool MarkAmbiguousMarginHeld(
        ulong newClOrdId, DateTimeOffset ambiguousAt, decimal newRemainingNotional)
    {
        if (_byNewClOrdId.TryGetValue(newClOrdId, out var found))
        {
            found.AmbiguousMarginHeld = true;
            found.AmbiguousAt = ambiguousAt;
            found.NewRemainingNotional = newRemainingNotional;
            // Pass-5 review (#299) P2. Add to the ambiguous-only
            // index that the sweep enumerates. TryAdd is idempotent
            // — a second mark on the same entry is a no-op here.
            _ambiguous.TryAdd(newClOrdId, 0);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Back-compat overload used by tests written before pass-5
    /// introduced the explicit <c>AmbiguousAt</c> anchor. Anchors the
    /// TTL to the entry's <see cref="Entry.CreatedAt"/> stamp (the
    /// pre-pass-5 sweep semantics). Production callers MUST use the
    /// timestamped overload so the WAL event and entry agree on the
    /// TTL anchor.
    /// </summary>
    public bool MarkAmbiguousMarginHeld(ulong newClOrdId)
    {
        if (_byNewClOrdId.TryGetValue(newClOrdId, out var found))
            return MarkAmbiguousMarginHeld(newClOrdId, found.CreatedAt);
        return false;
    }

    /// <summary>
    /// Pass-4 review (#299) P1. Remove + return every entry whose
    /// <see cref="Entry.AmbiguousMarginHeld"/> flag is set AND whose
    /// <see cref="Entry.AmbiguousAt"/> is older than <paramref name="now"/>
    /// minus <paramref name="ttl"/>. Caller (the AlgoScheduler sweep)
    /// is responsible for calling
    /// <see cref="Risk.IReplaceMarginCoordinator.AbortReplace"/> for
    /// each returned intent and bumping the expired-counter metric.
    /// Entries without the ambiguous flag (the normal in-flight state)
    /// are NEVER reaped — a long-lived modify on a slow venue is
    /// legitimate.
    /// <para>
    /// Pass-5 review (#299) P2. Iterates the dedicated ambiguous-only
    /// index (<c>_ambiguous</c>) instead of the full <c>_byNewClOrdId</c>
    /// map. Cost is O(ambiguous entries) — almost always zero in
    /// steady state — versus the previous O(all pending modifies).
    /// </para>
    /// </summary>
    public IReadOnlyList<OrderReplacementIntent> SweepExpiredAmbiguous(
        DateTimeOffset now, TimeSpan ttl)
    {
        Interlocked.Exchange(ref _lastSweepInspectedCount, 0);
        if (ttl <= TimeSpan.Zero) return Array.Empty<OrderReplacementIntent>();
        if (_ambiguous.IsEmpty) return Array.Empty<OrderReplacementIntent>();
        List<OrderReplacementIntent>? expired = null;
        var cutoff = now - ttl;
        foreach (var kvp in _ambiguous)
        {
            Interlocked.Increment(ref _lastSweepInspectedCount);
            if (!_byNewClOrdId.TryGetValue(kvp.Key, out var entry))
            {
                // Index entry survived its primary — defensive cleanup.
                _ambiguous.TryRemove(kvp.Key, out _);
                continue;
            }
            if (!entry.AmbiguousMarginHeld) continue;
            // Use AmbiguousAt as the age anchor (falls back to
            // CreatedAt for legacy entries that pre-date pass-5).
            var anchor = entry.AmbiguousAt ?? entry.CreatedAt;
            if (anchor > cutoff) continue;
            // Atomic remove guarded by the secondary index so a
            // racing TryConsume(newClOrdId) wins cleanly (only one
            // side will observe the entry as removable).
            if (_byNewClOrdId.TryRemove(kvp.Key, out var found))
            {
                _byOriginalClOrdId.TryRemove(found.Intent.OriginalClOrdId, out _);
                _ambiguous.TryRemove(kvp.Key, out _);
                (expired ??= new List<OrderReplacementIntent>()).Add(found.Intent);
            }
        }
        return (IReadOnlyList<OrderReplacementIntent>?)expired ?? Array.Empty<OrderReplacementIntent>();
    }

    /// <summary>Test/observability helper.</summary>
    internal int CountForTesting => _byNewClOrdId.Count;

    /// <summary>
    /// Pass-6 review (#299) P1. Lock-side snapshot of every in-flight
    /// entry — both plain in-flight and ambiguous-margin-held — for
    /// projection into <c>RawPlatformSnapshot.PendingReplacements</c>.
    /// The returned tuples carry every piece of state the post-restart
    /// <c>StateSnapshotter.Restore</c> needs to re-hydrate the entry
    /// AND (for ambiguous-flagged entries) re-invoke
    /// <c>IReplaceMarginCoordinator.PrepareReplaceAsync</c>.
    /// </summary>
    public IReadOnlyList<PendingReplacementEntrySnapshot> Snapshot()
    {
        var pairs = _byNewClOrdId.ToArray();
        if (pairs.Length == 0) return Array.Empty<PendingReplacementEntrySnapshot>();
        var snaps = new PendingReplacementEntrySnapshot[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            var e = pairs[i].Value;
            snaps[i] = new PendingReplacementEntrySnapshot(
                Intent: e.Intent,
                CreatedAt: e.CreatedAt,
                AmbiguousMarginHeld: e.AmbiguousMarginHeld,
                AmbiguousAt: e.AmbiguousAt,
                NewRemainingNotional: e.NewRemainingNotional);
        }
        return snaps;
    }

    /// <summary>
    /// Pass-6 review (#299) P1. Restores the registry from a
    /// snapshot. Wipes any prior state — restore happens before the
    /// WAL tail replay, which itself only re-adds entries the
    /// snapshot didn't carry. Caller is responsible for re-invoking
    /// <c>IReplaceMarginCoordinator.PrepareReplaceAsync</c> for every
    /// restored entry whose <see cref="PendingReplacementEntrySnapshot.AmbiguousMarginHeld"/>
    /// is set — see <c>StateSnapshotter.Restore</c>.
    /// </summary>
    public void Restore(IEnumerable<PendingReplacementEntrySnapshot> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _byNewClOrdId.Clear();
        _byOriginalClOrdId.Clear();
        _ambiguous.Clear();
        foreach (var s in entries)
        {
            var entry = new Entry(s.Intent, s.CreatedAt)
            {
                AmbiguousMarginHeld = s.AmbiguousMarginHeld,
                AmbiguousAt = s.AmbiguousAt,
                NewRemainingNotional = s.NewRemainingNotional,
            };
            if (!_byOriginalClOrdId.TryAdd(s.Intent.OriginalClOrdId, s.Intent.NewClOrdId))
                continue;
            if (!_byNewClOrdId.TryAdd(s.Intent.NewClOrdId, entry))
            {
                _byOriginalClOrdId.TryRemove(s.Intent.OriginalClOrdId, out _);
                continue;
            }
            if (s.AmbiguousMarginHeld)
                _ambiguous.TryAdd(s.Intent.NewClOrdId, 0);
        }
    }

    /// <summary>
    /// Pass-5 review (#299) P2 test hook. Number of registry entries
    /// the most recent <see cref="SweepExpiredAmbiguous"/> invocation
    /// inspected. Used by the unit test that asserts the sweep cost
    /// scales with the ambiguous-flagged count, not the total
    /// pending-modify count.
    /// </summary>
    internal long LastSweepInspectedCountForTesting =>
        Interlocked.Read(ref _lastSweepInspectedCount);

    /// <summary>
    /// Pass-5 review (#299) P2 test hook. Number of entries in the
    /// dedicated ambiguous-only index.
    /// </summary>
    internal int AmbiguousCountForTesting => _ambiguous.Count;
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

/// <summary>
/// Pass-6 review (#299) P1. Lock-side projection of one
/// <see cref="PendingReplacementRegistry"/> entry — both plain in-flight
/// and ambiguous-margin-held entries are captured. Travels via
/// <c>RawPlatformSnapshot.PendingReplacements</c> →
/// <c>PlatformSnapshot.PendingReplacements</c> so a snapshot taken
/// AFTER an ambiguous mark survives recovery even when the WAL tail
/// starts past the matching <c>OrderReplaceAmbiguousMarginHeldEvent</c>.
/// </summary>
public sealed record PendingReplacementEntrySnapshot(
    OrderReplacementIntent Intent,
    DateTimeOffset CreatedAt,
    bool AmbiguousMarginHeld,
    DateTimeOffset? AmbiguousAt,
    decimal NewRemainingNotional);
