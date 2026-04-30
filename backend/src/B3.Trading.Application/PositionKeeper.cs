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
}
