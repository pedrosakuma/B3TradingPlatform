using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Cumulative position keeper, derived from ExecutionReport fills. Per-firm,
/// per-end-client, per-symbol. Ephemeral in v1; rebuilt from ER replay on
/// (re)connect.
///
/// <para>
/// PR #316 P1. Adds the firm dimension to the internal key so the same JWT
/// <c>sub</c> (which becomes <see cref="EndClientId"/>) registered under
/// multiple firms (FIRM01, FIRM02) does NOT collide into a single per-symbol
/// row. The owner-scoped REST/WS read paths use the new
/// <see cref="ForEndClientAndFirm"/> variant to return only the caller's
/// firm slice. Legacy <c>ApplyFill(owner, …)</c> / <c>GetOrCreate(owner, …)</c>
/// / <c>SeedIfAbsent(owner, …)</c> overloads remain (delegating to
/// <see cref="DefaultFirmId"/>) so test compatibility and the
/// <c>PositionSeedOptions</c> startup seed (which does not carry firm) keep
/// working — the seed goes into the default-firm bucket and is read by the
/// default-firm code paths.
/// </para>
/// </summary>
public sealed class PositionKeeper
{
    /// <summary>
    /// PR #316 P1. Sentinel firm id used when a call site has not yet been
    /// migrated to the firm-aware API (legacy overloads, position seed from
    /// configuration, older snapshot rows that pre-date the firm dimension).
    /// </summary>
    /// <summary>
    /// Sentinel firm bucket used by the legacy no-firm overloads and
    /// by the snapshot DTO defaults (PR #316 P1 back-compat). Matches
    /// <see cref="B3.Trading.Domain.Order"/>'s ctor default so tests
    /// and any unfirmed call site converge on the same bucket.
    /// </summary>
    public const string DefaultFirmId = "DEFAULT";

    /// <summary>
    /// PR #316 P1. Firm ids in this codebase are treated case-insensitively
    /// at the keeper boundary: <c>JwtIssuer</c> emits <c>"default"</c> while
    /// <see cref="Domain.Order"/>'s ctor default is <c>"DEFAULT"</c>, and
    /// operator-supplied firm codes (FIRM01 vs firm01) must not split the
    /// same logical bucket. Every <c>firmId</c> parameter is normalised
    /// before it touches the dict key so all variants converge.
    /// </summary>
    internal static string NormalizeFirmId(string firmId) =>
        string.IsNullOrEmpty(firmId) ? DefaultFirmId : firmId.ToUpperInvariant();

    private readonly ConcurrentDictionary<(string FirmId, EndClientId Owner, string Symbol), Position> _positions = new();

    public Position GetOrCreate(EndClientId owner, string symbol) =>
        GetOrCreate(DefaultFirmId, owner, symbol);

    public Position GetOrCreate(string firmId, EndClientId owner, string symbol) =>
        _positions.GetOrAdd((NormalizeFirmId(firmId), owner, symbol), key => new Position(key.Owner, key.Symbol));

    /// <summary>
    /// Insert a starting position iff one is not already tracked for
    /// <paramref name="owner"/>/<paramref name="symbol"/>. Returns
    /// <c>true</c> when the seed was applied; <c>false</c> when an
    /// existing position (from snapshot/WAL replay or a prior fill)
    /// already occupies the slot. Idempotent and thread-safe.
    /// </summary>
    public bool SeedIfAbsent(EndClientId owner, string symbol, long netQuantity, decimal averageEntryPrice) =>
        SeedIfAbsent(DefaultFirmId, owner, symbol, netQuantity, averageEntryPrice);

    public bool SeedIfAbsent(string firmId, EndClientId owner, string symbol, long netQuantity, decimal averageEntryPrice)
    {
        var seeded = Position.Hydrate(owner, symbol, netQuantity, averageEntryPrice);
        return _positions.TryAdd((NormalizeFirmId(firmId), owner, symbol), seeded);
    }

    public void ApplyFill(EndClientId owner, string symbol, OrderSide side, long quantity, decimal price) =>
        ApplyFill(DefaultFirmId, owner, symbol, side, quantity, price);

    public void ApplyFill(string firmId, EndClientId owner, string symbol, OrderSide side, long quantity, decimal price)
    {
        var position = GetOrCreate(firmId, owner, symbol);
        lock (position)
        {
            position.ApplyFill(side, quantity, price);
        }
    }

    /// <summary>
    /// Returns positions for <paramref name="owner"/> across ALL firms.
    /// Preserved as legacy behaviour for callers we haven't migrated to
    /// the firm-aware API; owner-scoped REST/WS read paths MUST use
    /// <see cref="ForEndClientAndFirm"/> to avoid leaking cross-firm rows.
    /// </summary>
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

    /// <summary>
    /// PR #316 P1. Returns positions for <paramref name="owner"/> filtered
    /// to <paramref name="firmId"/>. Used by /api/positions, /api/pnl, /api/statement
    /// and the WS owner-scoped snapshot path so an end-client registered
    /// under multiple firms only sees the caller's firm slice.
    /// </summary>
    public IReadOnlyCollection<Position> ForEndClientAndFirm(string firmId, EndClientId owner)
    {
        var norm = NormalizeFirmId(firmId);
        var list = new List<Position>();
        foreach (var kv in _positions)
        {
            if (kv.Key.Owner == owner && string.Equals(kv.Key.FirmId, norm, StringComparison.Ordinal))
                list.Add(kv.Value);
        }
        return list;
    }

    /// <summary>
    /// Pass-1 review (#278) P1#3. Enumerates open (non-flat)
    /// positions for <paramref name="symbol"/> across every
    /// end-client. Used by the refprice → <c>pnl.me</c> fan-out to
    /// resolve which owners should receive an unrealized-P&amp;L
    /// delta when a symbol's mark moves. Returns a materialised list
    /// to free the caller from ToArray-on-iterate.
    /// </summary>
    public IReadOnlyList<Position> ForSymbol(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return Array.Empty<Position>();
        var list = new List<Position>();
        foreach (var kv in _positions)
        {
            if (kv.Key.Symbol == symbol && kv.Value.NetQuantity != 0)
                list.Add(kv.Value);
        }
        return list;
    }

    /// <summary>
    /// PR #316 P1. Same as <see cref="ForSymbol"/> but also returns the
    /// firmId for each row so per-(owner, firm) fan-out (e.g.
    /// <c>PnlRefPriceFanOut</c>) can publish a snapshot built from each
    /// client's own firm slice — required after the keeper grew the firm
    /// dimension, since the same owner can now appear in multiple firms
    /// for the same symbol.
    /// </summary>
    public IReadOnlyList<(string FirmId, Position Position)> ForSymbolWithFirm(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return Array.Empty<(string, Position)>();
        var list = new List<(string, Position)>();
        foreach (var kv in _positions)
        {
            if (kv.Key.Symbol == symbol && kv.Value.NetQuantity != 0)
                list.Add((kv.Key.FirmId, kv.Value));
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
                kv.Value.NetQuantity, kv.Value.AverageEntryPrice,
                kv.Key.FirmId);
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
                p.NetQuantity, p.AverageEntryPrice,
                pairs[i].Key.FirmId);
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
            var firmId = NormalizeFirmId(s.FirmId);
            _positions[(firmId, owner, s.Symbol)] =
                Position.Hydrate(owner, s.Symbol, s.NetQuantity, s.AverageEntryPrice);
        }
    }
}
