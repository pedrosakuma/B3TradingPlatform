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
        }
    }

    public void Remove(DropCopyClient client)
    {
        var firmId = client.FirmId;
        lock (LockFor(firmId))
        {
            _byFirm.AddOrUpdate(
                firmId,
                _ => ImmutableHashSet<DropCopyClient>.Empty,
                (_, set) => set.Remove(client));
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
}
