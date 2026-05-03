using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// In-memory aggregate of <see cref="Algo"/> parents, indexed by AlgoId
/// with secondary indices by end-client and firm. Mirrors the shape of
/// <see cref="WorkingOrderBook"/>: lock-free reads via
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>, snapshot/restore for
/// the persistence layer, and "current open parents for X" lookups for
/// the API/engine side.
///
/// <para>
/// Mutation of an individual <see cref="Algo"/> aggregate is the engine's
/// responsibility under the per-parent lock described in RFC §4.3 — the
/// book itself only synchronises the registration and de-registration.
/// </para>
/// </summary>
public sealed class AlgoBook
{
    private readonly ConcurrentDictionary<ulong, Algo> _algos = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>> _byOwner =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>> _byFirm =
        new(StringComparer.Ordinal);

    public bool TryAdd(Algo algo)
    {
        ArgumentNullException.ThrowIfNull(algo);
        if (!_algos.TryAdd(algo.AlgoId, algo))
            return false;

        var ownerSet = _byOwner.GetOrAdd(algo.Owner.Value, static _ => new ConcurrentDictionary<ulong, byte>());
        ownerSet.TryAdd(algo.AlgoId, 0);
        var firmSet = _byFirm.GetOrAdd(algo.FirmId, static _ => new ConcurrentDictionary<ulong, byte>());
        firmSet.TryAdd(algo.AlgoId, 0);
        return true;
    }

    public bool TryGet(ulong algoId, out Algo? algo) => _algos.TryGetValue(algoId, out algo);

    public IReadOnlyCollection<Algo> EnumerateForOwner(EndClientId owner, bool includeTerminal = false)
    {
        if (!_byOwner.TryGetValue(owner.Value, out var set))
            return Array.Empty<Algo>();
        var list = new List<Algo>(set.Count);
        foreach (var id in set.Keys)
        {
            if (!_algos.TryGetValue(id, out var a)) continue;
            if (!includeTerminal && a.IsTerminal) continue;
            list.Add(a);
        }
        return list;
    }

    public IReadOnlyCollection<Algo> EnumerateForFirm(string firmId, bool includeTerminal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        if (!_byFirm.TryGetValue(firmId, out var set))
            return Array.Empty<Algo>();
        var list = new List<Algo>(set.Count);
        foreach (var id in set.Keys)
        {
            if (!_algos.TryGetValue(id, out var a)) continue;
            if (!includeTerminal && a.IsTerminal) continue;
            list.Add(a);
        }
        return list;
    }

    /// <summary>
    /// Captures every algo (terminal included) for a snapshot. Same
    /// rationale as <see cref="WorkingOrderBook.Snapshot"/>: replay-from-WAL
    /// and replay-from-snapshot must converge on the same in-memory state.
    /// </summary>
    public IEnumerable<Persistence.AlgoSnapshot> Snapshot()
    {
        foreach (var kv in _algos)
        {
            var a = kv.Value;
            yield return ToSnapshot(a);
        }
    }

    public void Restore(IEnumerable<Persistence.AlgoSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _algos.Clear();
        _byOwner.Clear();
        _byFirm.Clear();
        foreach (var s in snaps)
        {
            var algo = FromSnapshot(s);
            _algos[algo.AlgoId] = algo;
            var ownerSet = _byOwner.GetOrAdd(algo.Owner.Value, static _ => new ConcurrentDictionary<ulong, byte>());
            ownerSet.TryAdd(algo.AlgoId, 0);
            var firmSet = _byFirm.GetOrAdd(algo.FirmId, static _ => new ConcurrentDictionary<ulong, byte>());
            firmSet.TryAdd(algo.AlgoId, 0);
        }
    }

    private static Persistence.AlgoSnapshot ToSnapshot(Algo a)
    {
        long? icebergDisplay = null;
        decimal? icebergLimit = null;
        DateTimeOffset? twapStart = null;
        DateTimeOffset? twapEnd = null;
        int? twapSliceCount = null;
        string? twapChildType = null;
        decimal? twapChildPrice = null;

        switch (a.Parameters)
        {
            case IcebergParameters ip:
                icebergDisplay = ip.DisplayQuantity;
                icebergLimit = ip.LimitPrice;
                break;
            case TwapParameters tp:
                twapStart = tp.StartUtc;
                twapEnd = tp.EndUtc;
                twapSliceCount = tp.SliceCount;
                twapChildType = tp.ChildOrderType.ToString();
                twapChildPrice = tp.ChildPrice;
                break;
        }

        return new Persistence.AlgoSnapshot(
            a.AlgoId, a.Owner.Value, a.FirmId, a.Symbol, a.SecurityId,
            a.Side.ToString(), a.Type.ToString(), a.TotalQuantity, a.FilledQuantity,
            a.Status.ToString(), a.TerminalReason.ToString(),
            a.CreatedAtUtc, a.TerminalAtUtc,
            icebergDisplay, icebergLimit,
            twapStart, twapEnd, twapSliceCount, twapChildType, twapChildPrice);
    }

    internal static Algo FromSnapshot(Persistence.AlgoSnapshot s)
    {
        var owner = new EndClientId(s.EndClientId);
        var side = Enum.Parse<OrderSide>(s.Side);
        var type = Enum.Parse<AlgoType>(s.Type);
        var status = Enum.Parse<AlgoStatus>(s.Status);
        var reason = Enum.Parse<AlgoTerminalReason>(s.TerminalReason);
        AlgoParameters parameters = type switch
        {
            AlgoType.Iceberg => new IcebergParameters(
                s.IcebergDisplayQuantity ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing IcebergDisplayQuantity."),
                s.IcebergLimitPrice),
            AlgoType.Twap => new TwapParameters(
                s.TwapStartUtc ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing TwapStartUtc."),
                s.TwapEndUtc ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing TwapEndUtc."),
                s.TwapSliceCount ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing TwapSliceCount."),
                Enum.Parse<OrderType>(s.TwapChildOrderType ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing TwapChildOrderType.")),
                s.TwapChildPrice),
            _ => throw new InvalidOperationException($"Unknown algo type: {s.Type}"),
        };
        return Algo.Hydrate(s.AlgoId, owner, s.FirmId, s.Symbol, s.SecurityId,
            side, type, s.TotalQuantity, parameters, s.CreatedAtUtc,
            s.FilledQuantity, status, reason, s.TerminalAtUtc);
    }
}
