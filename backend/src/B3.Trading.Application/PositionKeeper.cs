using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Cumulative position keeper, derived from ExecutionReport fills. Per-end-client,
/// per-symbol. Ephemeral in v1; rebuilt from ER replay on (re)connect.
/// </summary>
public sealed class PositionKeeper
{
    private readonly ConcurrentDictionary<(EndClientId Owner, string Symbol), Position> _positions = new();

    public Position GetOrCreate(EndClientId owner, string symbol) =>
        _positions.GetOrAdd((owner, symbol), key => new Position(key.Owner, key.Symbol));

    public void ApplyFill(EndClientId owner, string symbol, OrderSide side, long quantity, decimal price)
    {
        var position = GetOrCreate(owner, symbol);
        lock (position)
        {
            position.ApplyFill(side, quantity, price);
        }
    }

    public IReadOnlyCollection<Position> ForEndClient(EndClientId owner)
    {
        var list = new List<Position>();
        foreach (var kv in _positions)
        {
            if (kv.Key.Owner == owner)
                list.Add(kv.Value);
        }
        return list;
    }

    public IEnumerable<Persistence.PositionSnapshot> Snapshot()
    {
        foreach (var kv in _positions)
        {
            // Skip flat positions — they re-materialise the moment a fill
            // arrives, and persisting zero-quantity rows would bloat the
            // snapshot for no behavioural difference.
            if (kv.Value.NetQuantity == 0) continue;
            yield return new Persistence.PositionSnapshot(
                kv.Key.Owner.Value, kv.Key.Symbol,
                kv.Value.NetQuantity, kv.Value.AverageEntryPrice);
        }
    }

    public void Restore(IEnumerable<Persistence.PositionSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _positions.Clear();
        foreach (var s in snaps)
        {
            var owner = new EndClientId(s.EndClientId);
            _positions[(owner, s.Symbol)] = Position.Hydrate(owner, s.Symbol, s.NetQuantity, s.AverageEntryPrice);
        }
    }
}
