using System.Collections.Concurrent;
using System.Collections.Immutable;
using B3.Trading.Application;

namespace B3.Trading.Api.WebSockets.DropCopy;

/// <summary>
/// Q4.6 (#306). Per-firm subscriber registry + fan-out for the drop-copy
/// WebSocket feed. Mirrors the snapshot/delta atomicity contract of
/// <see cref="SubscriptionManager"/> but keys on firmId instead of an
/// owner identity — a single drop-copy session observes every
/// order/fill/cancel for its firm, regardless of which user originated
/// the event.
///
/// <para><b>Atomicity contract.</b> Subscribe takes a per-firm lock and
/// enqueues the initial snapshot frame for each requested channel
/// under that lock; subsequent <see cref="Publish"/> calls also take
/// the same per-firm lock when fanning out to that firm's subscribers.
/// A delta published concurrently with the subscribe therefore lands
/// AFTER the snapshot on the subscriber's outbound channel — same
/// pattern <c>SubscribeWithSnapshot</c> uses on the per-user hub
/// (see RFC §4.3 / RFC §5.2 ordering notes).</para>
/// </summary>
public sealed class DropCopyManager
{
    /// <summary>Logical channel names exposed by the drop-copy feed.</summary>
    public static class DropCopyChannels
    {
        public const string Orders = "dropcopy.orders";
        public const string Fills = "dropcopy.fills";
        public const string Cancels = "dropcopy.cancels";

        public static readonly IReadOnlyList<string> All = new[] { Orders, Fills, Cancels };
    }

    private readonly ConcurrentDictionary<string, ImmutableHashSet<DropCopyClient>> _byFirm =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _firmLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // Pass-6 review (#323) — per-firm armed gate, NOT global.
    // Coalesces overflow drops: itemDropped fires per dropped event
    // under EventDispatcher's global dispatch lock; with N concurrent
    // drops in a burst we don't want O(drops × firms × subscribers).
    //
    // Atomicity (vs the prior global gate): a global flag let a drop
    // race the post-insert/pre-arm window in Add() and silently miss a
    // freshly registered client. Per-firm flag fixes that — registration
    // (bucket insert) AND arm happen under LockFor(firmId), and the
    // disconnect walk consumes the flag under the SAME lock, so
    // visibility + arm + consume are atomic relative to the firm-lock.
    //
    // Cost on drop storms: O(firms) uncontended TryGetValue + lock +
    // Volatile.Read per drop after the first; the per-firm lock is the
    // same one Publish takes, so we trade against (already-backpressured)
    // publish throughput, not against unrelated firms.
    private readonly ConcurrentDictionary<string, int> _firmResyncArmed =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly WorkingOrderBook _orders;

    public DropCopyManager(WorkingOrderBook orders)
    {
        _orders = orders;
    }

    private object LockFor(string firmId) =>
        _firmLocks.GetOrAdd(firmId, _ => new object());

    /// <summary>
    /// Atomically registers <paramref name="client"/> for every drop-copy
    /// channel in <see cref="DropCopyChannels.All"/> and enqueues the
    /// initial snapshot frame for each (orders = firm-scoped working
    /// order DTOs; fills/cancels = empty — no historical exec log in
    /// v1, matching <c>executions.me</c>). The per-firm lock ensures
    /// any concurrent <see cref="Publish"/> for the same firm queues
    /// AFTER the snapshots.
    /// </summary>
    public void Add(DropCopyClient client)
    {
        var firmId = client.FirmId;
        lock (LockFor(firmId))
        {
            _byFirm.AddOrUpdate(
                firmId,
                _ => ImmutableHashSet.Create(client),
                (_, set) => set.Add(client));

            // Orders snapshot: every non-terminal working order in the firm.
            var orderDtos = _orders.EnumerateForFirm(firmId).Select(o => o.ToDto()).ToArray();

            if (client.Subscribe(DropCopyChannels.Orders))
                client.Enqueue(new OutboundMessage("snapshot", DropCopyChannels.Orders, 0, orderDtos));
            if (client.Subscribe(DropCopyChannels.Fills))
                client.Enqueue(new OutboundMessage("snapshot", DropCopyChannels.Fills, 0, Array.Empty<ExecutionDto>()));
            if (client.Subscribe(DropCopyChannels.Cancels))
                client.Enqueue(new OutboundMessage("snapshot", DropCopyChannels.Cancels, 0, Array.Empty<ExecutionDto>()));

            // Arm the per-firm resync gate UNDER THE SAME LOCK as the
            // bucket insert above. The disconnect walk consumes this
            // gate under the same lock, so a concurrent drop either
            // (a) acquires the lock AFTER us and sees the new client
            // and the armed gate, or (b) acquires the lock BEFORE us
            // and walks subscribers that do NOT include this client —
            // which is correct, because the event that triggered the
            // drop happened before we became visible in _byFirm and
            // therefore wasn't ours to receive.
            _firmResyncArmed[firmId] = 1;
        }
    }

    public void Remove(DropCopyClient client)
    {
        var firmId = client.FirmId;
        lock (LockFor(firmId))
        {
            if (!_byFirm.TryGetValue(firmId, out var set))
                return;
            set = set.Remove(client);
            if (set.IsEmpty)
            {
                // Pass-7 review (#323) P2: with admin firmId override
                // accepting arbitrary tenant strings, leaving an empty
                // bucket per ever-seen firmId is a process-lifetime
                // dictionary growth vector. Clean up _byFirm and
                // _firmResyncArmed when the last subscriber leaves.
                // _firmLocks intentionally retained — removing it would
                // race concurrent Add()s that already captured the lock
                // reference and could end up serializing on different
                // monitors. The monitor object itself is ~24 bytes and
                // bounded growth is acceptable for the rare admin
                // override path (also gated by firmId validation in the
                // hub).
                _byFirm.TryRemove(firmId, out _);
                _firmResyncArmed.TryRemove(firmId, out _);
            }
            else
            {
                _byFirm[firmId] = set;
            }
        }
    }

    /// <summary>
    /// Fans out <paramref name="payload"/> as a delta on
    /// <paramref name="channel"/> to every drop-copy subscriber of
    /// <paramref name="firmId"/>. No-op when no subscriber exists for
    /// the firm (the common case — drop-copy sessions are rare).
    /// </summary>
    public void Publish(string firmId, string channel, object payload)
    {
        if (string.IsNullOrEmpty(firmId)) return;

        // Atomicity contract (Q4.6 / RFC §4.3): the per-firm lock is the
        // single serialization point between subscriber registration
        // (Add) and delta fan-out. We must take the lock BEFORE reading
        // _byFirm — otherwise a concurrent Add() could register a new
        // subscriber + enqueue its snapshot frame while we are still
        // holding a stale subscriber set, causing the racing delta to
        // be silently lost for the new subscriber. The empty-set
        // early-out happens INSIDE the lock so it remains cheap when
        // no compliance session is active (the common case).
        lock (LockFor(firmId))
        {
            if (!_byFirm.TryGetValue(firmId, out var clients) || clients.IsEmpty)
                return;

            foreach (var client in clients)
            {
                if (client.MarkedForDisconnect) continue;
                if (!client.IsSubscribed(channel)) continue;
                var seq = client.NextSeq(channel);
                if (seq < 0) continue;
                client.Enqueue(new OutboundMessage("delta", channel, seq, payload));
            }
        }
    }

    /// <summary>
    /// Diagnostic: number of currently-registered subscribers for
    /// <paramref name="firmId"/>. Used by the disconnect-cleanup test
    /// to assert no leak.
    /// </summary>
    public int SubscriberCount(string firmId) =>
        _byFirm.TryGetValue(firmId, out var set) ? set.Count : 0;

    /// <summary>
    /// Fail-closed when an upstream component (e.g. the bounded
    /// fan-out sink) detects an event drop that would otherwise be
    /// invisible to subscribers. Marks every currently-registered
    /// drop-copy session — across ALL firms — for resync disconnect
    /// with <paramref name="reason"/>. The hub teardown observes the
    /// per-client signal and runs the standard cleanup; clients then
    /// reconnect, receive a fresh snapshot, and resume from a known
    /// state. Pass-3 review (#323): fills/cancels snapshots are
    /// empty by design, so a silent <c>DropOldest</c> on the sink
    /// channel would lose an event forever even though per-client
    /// seqs stayed contiguous.
    /// </summary>
    public void DisconnectAllForResync(string reason)
    {
        // Per-firm coalesce: arm is set under LockFor(firmId) by Add,
        // and we consume it under the SAME lock here. NO unlocked
        // fast-path: a stale armed==0 read outside the lock could race
        // a registration-in-progress and skip the firm entirely (pass-7
        // P1). The firm count is bounded by tenant cardinality so the
        // per-drop O(firms) lock acquisition is acceptable.
        foreach (var firmId in _firmResyncArmed.Keys)
        {
            lock (LockFor(firmId))
            {
                if (!_firmResyncArmed.TryGetValue(firmId, out var armed) || armed == 0)
                    continue;
                _firmResyncArmed[firmId] = 0;

                if (!_byFirm.TryGetValue(firmId, out var clients) || clients.IsEmpty)
                    continue;
                foreach (var c in clients)
                    c.RequestResyncDisconnect(reason);
            }
        }
    }
}
