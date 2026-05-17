using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// In-memory aggregate of <see cref="Algo"/> parents, indexed by
/// <c>(firmId, algoId)</c> with secondary indices by end-client and
/// firm. Mirrors the firm-isolation pattern used by the rest of the
/// stateful platform components — two firms can independently issue
/// <c>AlgoId = 1</c> without collision because every public surface
/// (HTTP route, WS payload, snapshot record) carries the firm context
/// derived from the caller's auth claim.
///
/// <para>
/// Mutation of an individual <see cref="Algo"/> aggregate is the engine's
/// responsibility under the per-parent lock described in RFC §4.3 — the
/// book itself only synchronises the registration and de-registration.
/// </para>
/// </summary>
public sealed class AlgoBook
{
    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), Algo> _algos = new();
    private readonly ConcurrentDictionary<(string FirmId, string Owner), ConcurrentDictionary<ulong, byte>> _byOwner = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>> _byFirm =
        new(StringComparer.Ordinal);

    public bool TryAdd(Algo algo)
    {
        ArgumentNullException.ThrowIfNull(algo);
        if (!_algos.TryAdd((algo.FirmId, algo.AlgoId), algo))
            return false;

        var ownerSet = _byOwner.GetOrAdd((algo.FirmId, algo.Owner.Value),
            static _ => new ConcurrentDictionary<ulong, byte>());
        ownerSet.TryAdd(algo.AlgoId, 0);
        var firmSet = _byFirm.GetOrAdd(algo.FirmId, static _ => new ConcurrentDictionary<ulong, byte>());
        firmSet.TryAdd(algo.AlgoId, 0);
        return true;
    }

    public bool TryGet(string firmId, ulong algoId, out Algo? algo) =>
        _algos.TryGetValue((firmId, algoId), out algo);

    public IReadOnlyCollection<Algo> EnumerateForOwner(string firmId, EndClientId owner, bool includeTerminal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        if (!_byOwner.TryGetValue((firmId, owner.Value), out var set))
            return Array.Empty<Algo>();
        var list = new List<Algo>(set.Count);
        foreach (var id in set.Keys)
        {
            if (!_algos.TryGetValue((firmId, id), out var a)) continue;
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
            if (!_algos.TryGetValue((firmId, id), out var a)) continue;
            if (!includeTerminal && a.IsTerminal) continue;
            list.Add(a);
        }
        return list;
    }

    public IReadOnlyCollection<Algo> EnumerateAll(bool includeTerminal = false)
    {
        var list = new List<Algo>(_algos.Count);
        foreach (var kv in _algos)
        {
            if (!includeTerminal && kv.Value.IsTerminal) continue;
            list.Add(kv.Value);
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

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8 / P6). Captures every algo's mutable scalars
    /// (<c>FilledQuantity</c>, <c>Status</c>, <c>TerminalReason</c>,
    /// <c>TerminalAtUtc</c>) by value while the caller still holds the
    /// dispatcher lock; immutable construction fields are read off the
    /// captured <see cref="Algo"/> reference during projection. Same
    /// §4.3 invariant as <c>WorkingOrderBook.RawSnapshot</c>.
    /// </summary>
    public Persistence.AlgoRaw[] RawSnapshot()
    {
        var pairs = _algos.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.AlgoRaw>();
        var raw = new Persistence.AlgoRaw[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            var a = pairs[i].Value;
            raw[i] = new Persistence.AlgoRaw(a, a.FilledQuantity, a.Status, a.TerminalReason, a.TerminalAtUtc);
        }
        return raw;
    }

    /// <summary>
    /// Phase-2 projection of a <see cref="Persistence.AlgoRaw"/> captured
    /// by <see cref="RawSnapshot"/>. Pulls the immutable construction-time
    /// fields off the live <see cref="Algo"/> reference and the mutable
    /// scalars from the raw struct, so the result is consistent with the
    /// snapshot's <c>seq</c> (RFC §4.3) even though it runs outside the
    /// dispatcher lock.
    /// </summary>
    internal static Persistence.AlgoSnapshot ProjectRaw(Persistence.AlgoRaw r)
    {
        var a = r.Algo;
        long? icebergDisplay = null;
        decimal? icebergLimit = null;
        DateTimeOffset? twapStart = null;
        DateTimeOffset? twapEnd = null;
        int? twapSliceCount = null;
        string? twapChildType = null;
        decimal? twapChildPrice = null;
        DateTimeOffset? vwapStart = null;
        DateTimeOffset? vwapEnd = null;
        string? vwapChildType = null;
        decimal? vwapChildPrice = null;
        long? vwapTickIntervalTicks = null;
        decimal? vwapSliceMaxPct = null;
        decimal? vwapPriceLimit = null;
        decimal? vwapParticipationCap = null;
        DateTimeOffset? povStart = null;
        DateTimeOffset? povEnd = null;
        string? povChildType = null;
        decimal? povChildPrice = null;
        decimal? povParticipationRate = null;
        long? povTickIntervalTicks = null;
        decimal? povPriceLimit = null;
        long? povMinSliceQty = null;

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
            case VwapParameters vp:
                vwapStart = vp.StartUtc;
                vwapEnd = vp.EndUtc;
                vwapChildType = vp.ChildOrderType.ToString();
                vwapChildPrice = vp.ChildPrice;
                vwapTickIntervalTicks = vp.TickInterval.Ticks;
                vwapSliceMaxPct = vp.SliceMaxPct;
                vwapPriceLimit = vp.PriceLimit;
                vwapParticipationCap = vp.ParticipationCap;
                break;
            case PovParameters pp:
                povStart = pp.StartUtc;
                povEnd = pp.EndUtc;
                povChildType = pp.ChildOrderType.ToString();
                povChildPrice = pp.ChildPrice;
                povParticipationRate = pp.ParticipationRate;
                povTickIntervalTicks = pp.TickInterval.Ticks;
                povPriceLimit = pp.PriceLimit;
                povMinSliceQty = pp.MinSliceQty;
                break;
        }

        return new Persistence.AlgoSnapshot(
            a.AlgoId, a.Owner.Value, a.FirmId, a.Symbol, a.SecurityId,
            a.Side.ToString(), a.Type.ToString(), a.TotalQuantity, r.Filled,
            r.Status.ToString(), r.Reason.ToString(),
            a.CreatedAtUtc, r.TerminalAtUtc,
            icebergDisplay, icebergLimit,
            twapStart, twapEnd, twapSliceCount, twapChildType, twapChildPrice,
            vwapStart, vwapEnd, vwapChildType, vwapChildPrice,
            vwapTickIntervalTicks, vwapSliceMaxPct, vwapPriceLimit, vwapParticipationCap,
            povStart, povEnd, povChildType, povChildPrice,
            povParticipationRate, povTickIntervalTicks, povPriceLimit, povMinSliceQty);
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
            _algos[(algo.FirmId, algo.AlgoId)] = algo;
            var ownerSet = _byOwner.GetOrAdd((algo.FirmId, algo.Owner.Value),
                static _ => new ConcurrentDictionary<ulong, byte>());
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
        DateTimeOffset? vwapStart = null;
        DateTimeOffset? vwapEnd = null;
        string? vwapChildType = null;
        decimal? vwapChildPrice = null;
        long? vwapTickIntervalTicks = null;
        decimal? vwapSliceMaxPct = null;
        decimal? vwapPriceLimit = null;
        decimal? vwapParticipationCap = null;
        DateTimeOffset? povStart = null;
        DateTimeOffset? povEnd = null;
        string? povChildType = null;
        decimal? povChildPrice = null;
        decimal? povParticipationRate = null;
        long? povTickIntervalTicks = null;
        decimal? povPriceLimit = null;
        long? povMinSliceQty = null;

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
            case VwapParameters vp:
                vwapStart = vp.StartUtc;
                vwapEnd = vp.EndUtc;
                vwapChildType = vp.ChildOrderType.ToString();
                vwapChildPrice = vp.ChildPrice;
                vwapTickIntervalTicks = vp.TickInterval.Ticks;
                vwapSliceMaxPct = vp.SliceMaxPct;
                vwapPriceLimit = vp.PriceLimit;
                vwapParticipationCap = vp.ParticipationCap;
                break;
            case PovParameters pp:
                povStart = pp.StartUtc;
                povEnd = pp.EndUtc;
                povChildType = pp.ChildOrderType.ToString();
                povChildPrice = pp.ChildPrice;
                povParticipationRate = pp.ParticipationRate;
                povTickIntervalTicks = pp.TickInterval.Ticks;
                povPriceLimit = pp.PriceLimit;
                povMinSliceQty = pp.MinSliceQty;
                break;
        }

        return new Persistence.AlgoSnapshot(
            a.AlgoId, a.Owner.Value, a.FirmId, a.Symbol, a.SecurityId,
            a.Side.ToString(), a.Type.ToString(), a.TotalQuantity, a.FilledQuantity,
            a.Status.ToString(), a.TerminalReason.ToString(),
            a.CreatedAtUtc, a.TerminalAtUtc,
            icebergDisplay, icebergLimit,
            twapStart, twapEnd, twapSliceCount, twapChildType, twapChildPrice,
            vwapStart, vwapEnd, vwapChildType, vwapChildPrice,
            vwapTickIntervalTicks, vwapSliceMaxPct, vwapPriceLimit, vwapParticipationCap,
            povStart, povEnd, povChildType, povChildPrice,
            povParticipationRate, povTickIntervalTicks, povPriceLimit, povMinSliceQty);
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
            AlgoType.Vwap => new VwapParameters(
                s.VwapStartUtc ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing VwapStartUtc."),
                s.VwapEndUtc ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing VwapEndUtc."),
                Enum.Parse<OrderType>(s.VwapChildOrderType ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing VwapChildOrderType.")),
                s.VwapChildPrice,
                TimeSpan.FromTicks(s.VwapTickIntervalTicks ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing VwapTickIntervalTicks.")),
                s.VwapSliceMaxPct,
                s.VwapPriceLimit,
                s.VwapParticipationCap),
            AlgoType.Pov => new PovParameters(
                s.PovStartUtc ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing PovStartUtc."),
                s.PovEndUtc ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing PovEndUtc."),
                Enum.Parse<OrderType>(s.PovChildOrderType ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing PovChildOrderType.")),
                s.PovChildPrice,
                s.PovParticipationRate ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing PovParticipationRate."),
                TimeSpan.FromTicks(s.PovTickIntervalTicks ?? throw new InvalidOperationException($"Algo {s.AlgoId} snapshot missing PovTickIntervalTicks.")),
                s.PovPriceLimit,
                s.PovMinSliceQty ?? 1L),
            _ => throw new InvalidOperationException($"Unknown algo type: {s.Type}"),
        };
        return Algo.Hydrate(s.AlgoId, owner, s.FirmId, s.Symbol, s.SecurityId,
            side, type, s.TotalQuantity, parameters, s.CreatedAtUtc,
            s.FilledQuantity, status, reason, s.TerminalAtUtc);
    }
}
