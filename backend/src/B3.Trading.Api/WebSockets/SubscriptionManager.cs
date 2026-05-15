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

    // Q1.5 (#257). Parallel registry for non-owner-scoped (public)
    // per-symbol channels (phases.${symbol} / auction.${symbol}).
    // Same per-channel lock model as the owner side; the snapshot
    // supplier is invoked under the lock so the snapshot frame is
    // enqueued atomically with the registration.
    private readonly ConcurrentDictionary<string, ImmutableHashSet<SubscribedClient>> _byPublicChannel =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _publicChannelLocks =
        new(StringComparer.Ordinal);

    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;
    private readonly AlgoBook _algos;
    private readonly PnlKeeper? _pnl;
    private readonly Application.Risk.IReferencePrice? _refPrice;

    public SubscriptionManager(
        WorkingOrderBook orders,
        PositionKeeper positions,
        AlgoBook algos,
        PnlKeeper? pnl = null,
        Application.Risk.IReferencePrice? refPrice = null)
    {
        _orders = orders;
        _positions = positions;
        _algos = algos;
        _pnl = pnl;
        _refPrice = refPrice;
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
                Channels.PnlMe => (_pnl is not null && _refPrice is not null)
                    ? PnlProjection.Build(client.Owner, _pnl, _positions, _refPrice)
                    : null,
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

    // ----------------------------------------------------------------
    // Q1.5 (#257). Public per-symbol channels (phases.* / auction.*).
    // No owner scoping — fan-out is to every subscribed client of the
    // channel string. Snapshot supplier is a delegate so the manager
    // doesn't have to know about market-data shapes.
    // ----------------------------------------------------------------

    private object PublicLockFor(string channel) =>
        _publicChannelLocks.GetOrAdd(channel, _ => new object());

    /// <summary>
    /// Subscribes <paramref name="client"/> to public
    /// <paramref name="channel"/> and atomically enqueues the initial
    /// snapshot frame produced by <paramref name="snapshotFactory"/>.
    /// Returning <c>null</c> from the factory enqueues an empty
    /// snapshot — that's the right shape when nothing has been
    /// observed yet (a typical empty book / no-phase-known case).
    /// </summary>
    public void SubscribePublicWithSnapshot(
        SubscribedClient client,
        string channel,
        Func<object?> snapshotFactory)
    {
        lock (PublicLockFor(channel))
        {
            if (!client.Subscribe(channel))
                return;

            _byPublicChannel.AddOrUpdate(
                channel,
                _ => ImmutableHashSet.Create(client),
                (_, set) => set.Add(client));

            object? snapshot;
            try { snapshot = snapshotFactory(); }
            catch { snapshot = null; }

            client.Enqueue(new OutboundMessage("snapshot", channel, 0, snapshot));
        }
    }

    /// <summary>
    /// Fan out <paramref name="payload"/> as a delta on public
    /// <paramref name="channel"/> to every subscribed client.
    /// </summary>
    public void BroadcastPublic(string channel, object payload)
    {
        if (!_byPublicChannel.TryGetValue(channel, out var clients) || clients.IsEmpty)
            return;

        lock (PublicLockFor(channel))
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

    /// <summary>
    /// Removes a client from every public channel it was subscribed
    /// to. Safe to call when the client never subscribed.
    /// </summary>
    public void RemoveFromPublic(SubscribedClient client)
    {
        // Public channel registry isn't keyed by client, so we walk it.
        // Cheap in steady state (few channels relative to clients).
        foreach (var channel in _byPublicChannel.Keys)
        {
            if (!client.IsSubscribed(channel))
                continue;
            lock (PublicLockFor(channel))
            {
                _byPublicChannel.AddOrUpdate(
                    channel,
                    _ => ImmutableHashSet<SubscribedClient>.Empty,
                    (_, set) => set.Remove(client));
            }
        }
    }

    public int PublicSubscriberCount(string channel) =>
        _byPublicChannel.TryGetValue(channel, out var set) ? set.Count : 0;
}
