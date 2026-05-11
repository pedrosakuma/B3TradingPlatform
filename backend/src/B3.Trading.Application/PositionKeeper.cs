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

    /// <summary>
    /// Insert a starting position iff one is not already tracked for
    /// <paramref name="owner"/>/<paramref name="symbol"/>. Returns
    /// <c>true</c> when the seed was applied; <c>false</c> when an
    /// existing position (from snapshot/WAL replay or a prior fill)
    /// already occupies the slot. Idempotent and thread-safe.
    /// </summary>
    public bool SeedIfAbsent(EndClientId owner, string symbol, long netQuantity, decimal averageEntryPrice)
    {
        var seeded = Position.Hydrate(owner, symbol, netQuantity, averageEntryPrice);
        return _positions.TryAdd((owner, symbol), seeded);
    }

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

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8 / P6). Same flat-position skip as <see cref="Snapshot"/>.
    /// Caller must hold <c>EventDispatcher.WithSnapshotLock</c> so the
    /// scalar reads of <c>NetQuantity</c> / <c>AverageEntryPrice</c>
    /// reflect the snapshot's <c>seq</c> (RFC §4.3).
    /// </summary>
    public Persistence.PositionRaw[] RawSnapshot()
    {
        var pairs = _positions.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.PositionRaw>();
        var buf = new Persistence.PositionRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            var p = pairs[i].Value;
            if (p.NetQuantity == 0) continue;
            buf[n++] = new Persistence.PositionRaw(
                pairs[i].Key.Owner.Value, pairs[i].Key.Symbol,
                p.NetQuantity, p.AverageEntryPrice);
        }
        if (n == buf.Length) return buf;
        var trimmed = new Persistence.PositionRaw[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
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
