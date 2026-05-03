using System.Collections.Concurrent;
using System.Collections.Immutable;
using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Per-end-client subscription registry + fan-out. Subscribe and publish
/// take a per-owner lock so the snapshot frame is enqueued atomically with
/// the registration: any subsequent delta for the same owner is enqueued
/// AFTER the snapshot. Deltas mutate state idempotently (apply-by-clOrdId)
/// so a tiny race in which a delta reflects a state already in the snapshot
/// is tolerable for v1 clients.
/// </summary>
public sealed class SubscriptionManager
{
    private readonly ConcurrentDictionary<EndClientId, ImmutableHashSet<SubscribedClient>> _byOwner = new();
    private readonly ConcurrentDictionary<EndClientId, object> _ownerLocks = new();
    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;
    private readonly AlgoBook _algos;

    public SubscriptionManager(WorkingOrderBook orders, PositionKeeper positions, AlgoBook algos)
    {
        _orders = orders;
        _positions = positions;
        _algos = algos;
    }

    private object LockFor(EndClientId owner) =>
        _ownerLocks.GetOrAdd(owner, _ => new object());

    public void Add(SubscribedClient client)
    {
        lock (LockFor(client.Owner))
        {
            _byOwner.AddOrUpdate(
                client.Owner,
                _ => ImmutableHashSet.Create(client),
                (_, set) => set.Add(client));
        }
    }

    public void Remove(SubscribedClient client)
    {
        lock (LockFor(client.Owner))
        {
            _byOwner.AddOrUpdate(
                client.Owner,
                _ => ImmutableHashSet<SubscribedClient>.Empty,
                (_, set) => set.Remove(client));
        }
    }

    /// <summary>
    /// Subscribes <paramref name="client"/> to <paramref name="channel"/>
    /// and atomically enqueues the initial snapshot frame.
    /// </summary>
    public void SubscribeWithSnapshot(SubscribedClient client, string channel)
    {
        lock (LockFor(client.Owner))
        {
            if (!client.Subscribe(channel))
                return; // already subscribed; snapshot is implicitly already-sent

            object? data = channel switch
            {
                Channels.OrdersMe => _orders.ForEndClient(client.Owner).Select(o => o.ToDto()).ToArray(),
                Channels.PositionsMe => _positions.ForEndClient(client.Owner).Select(p => p.ToDto()).ToArray(),
                Channels.ExecutionsMe => Array.Empty<ExecutionDto>(), // no historical exec log in v1
                Channels.AlgoMe => _algos.EnumerateForOwner(client.FirmId, client.Owner).Select(a => a.ToDto()).ToArray(),
                _ => null,
            };

            client.Enqueue(new OutboundMessage("snapshot", channel, 0, data));
        }
    }

    /// <summary>
    /// Fan out <paramref name="payload"/> as a delta on <paramref name="channel"/>
    /// to every subscribed client of <paramref name="owner"/>.
    /// </summary>
    public void Publish(EndClientId owner, string channel, object payload)
    {
        if (!_byOwner.TryGetValue(owner, out var clients) || clients.IsEmpty)
            return;

        lock (LockFor(owner))
        {
            foreach (var client in clients)
            {
                if (!client.IsSubscribed(channel) || client.MarkedForDisconnect)
                    continue;
                var seq = client.NextSeq(channel);
                if (seq < 0)
                    continue;
                client.Enqueue(new OutboundMessage("delta", channel, seq, payload));
            }
        }
    }

    public int CountFor(EndClientId owner) =>
        _byOwner.TryGetValue(owner, out var set) ? set.Count : 0;
}
