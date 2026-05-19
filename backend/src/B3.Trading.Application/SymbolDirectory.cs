namespace B3.Trading.Application;

/// <summary>
/// Static symbol → SecurityId map bound from
/// <c>Trading:SymbolDirectory:SecurityIds</c>. The trader UI submits
/// orders by symbol because that is what end-clients understand;
/// B3 wire (BinaryEntryPoint / SBE) addresses instruments by their
/// numeric SecurityId. Without a directory, every UI submit is
/// rejected with <c>securityId is required</c>.
/// </summary>
/// <remarks>
/// Resolution rules (see <see cref="OrdersEndpoints.MapPost"/> in
/// <c>B3.Trading.Api</c>):
/// <list type="number">
///   <item>If the request payload carries a non-zero
///   <c>SecurityId</c>, that wins (preserves the conformance suite
///   contract — explicit values are never overridden).</item>
///   <item>Otherwise the directory is consulted by symbol; case
///   does not matter (<see cref="StringComparer.OrdinalIgnoreCase"/>).</item>
///   <item>If neither path yields an id, the endpoint still returns
///   a 400 with the same message. The directory is additive, not a
///   silent fallback.</item>
/// </list>
/// The directory is intentionally simple in v1 (in-process, read at
/// startup). When the participant on-boards instruments dynamically
/// (e.g. via a real B3 Security Definition feed), a hot-reload or
/// service-backed implementation will replace this class behind the
/// same <see cref="TryResolve(string, out ulong)"/> API.
/// </remarks>
public sealed class SymbolDirectory
{
    private readonly IReadOnlyDictionary<string, ulong> _byName;
    private readonly IReadOnlyDictionary<ulong, string> _bySecurityId;
    private readonly IReadOnlyDictionary<string, InstrumentSpec> _specs;

    public SymbolDirectory(SymbolDirectoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Always copy to enforce case-insensitive comparison even if
        // the binder produced a culture-sensitive dictionary.
        var copy = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        // Sub-issue #171 (E): inverse lookup (SecurityId → Symbol) for the
        // FIXP adapter, which receives orders by numeric SecurityId on the
        // wire and must translate to the Symbol the submit pipeline
        // expects. Computed at construction time from the same forward
        // map — no extra config surface.
        var inverse = new Dictionary<ulong, string>();
        foreach (var kv in options.SecurityIds)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == 0) continue;
            copy[kv.Key] = kv.Value;
            // First-write-wins: if two symbols claim the same SecurityId
            // (configuration mistake), the inverse map keeps the first
            // one we saw rather than silently overwriting. The forward
            // map remains correct for both.
            inverse.TryAdd(kv.Value, kv.Key);
        }
        _byName = copy;
        _bySecurityId = inverse;

        var specs = new Dictionary<string, InstrumentSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in options.Specs)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var t = kv.Value.TickSize;
            var l = kv.Value.LotSize;
            var ladder = NormalizeLadder(kv.Value.TickLadder);
            // Drop entries that wouldn't constrain anything — keeps
            // TryGetSpec callers from having to special-case zero/negative.
            if ((t is null or <= 0m) && (l is null or <= 0) && ladder is null) continue;
            specs[kv.Key] = new InstrumentSpec(
                t is > 0m ? t : null,
                l is > 0 ? l : null,
                ladder);
        }
        _specs = specs;
    }

    // #360. Validate and canonicalize the optional CVM-style tick
    // ladder: drop malformed rows (non-positive tick, NaN), de-dup by
    // MinPriceInclusive (last-write-wins per band), sort ascending so
    // ResolveTick can do a binary search. Returns null when the
    // resulting ladder is empty so MinTickSizeCheck can treat
    // "ladder present" as a positive signal.
    private static IReadOnlyList<TickBand>? NormalizeLadder(IReadOnlyList<TickBandOptions>? rows)
    {
        if (rows is null || rows.Count == 0) return null;
        var sorted = new SortedDictionary<decimal, decimal>();
        foreach (var r in rows)
        {
            if (r is null) continue;
            if (r.Tick <= 0m) continue;
            if (r.MinPriceInclusive < 0m) continue;
            sorted[r.MinPriceInclusive] = r.Tick;
        }
        if (sorted.Count == 0) return null;
        var list = new TickBand[sorted.Count];
        var i = 0;
        foreach (var kv in sorted) list[i++] = new TickBand(kv.Key, kv.Value);
        return list;
    }

    public int Count => _byName.Count;

    public bool TryResolve(string? symbol, out ulong securityId)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            securityId = 0;
            return false;
        }
        return _byName.TryGetValue(symbol, out securityId);
    }

    /// <summary>
    /// Sub-issue #171 (E). Inverse of <see cref="TryResolve(string, out ulong)"/>:
    /// resolves a numeric SecurityId back to its configured symbol. The
    /// FIXP listener calls this on every <c>NewOrderSingle</c> /
    /// <c>OrderCancelRequest</c> because the SBE wire only carries the
    /// SecurityId, but <c>OrderSubmissionService</c> requires a non-empty
    /// symbol. Returns <c>false</c> for unknown ids — the listener turns
    /// that into a <c>BusinessMessageReject(UnknownSecurity)</c> without
    /// touching the submit pipeline.
    /// </summary>
    public bool TryGetSymbolBySecurityId(ulong securityId, out string? symbol)
    {
        if (securityId == 0)
        {
            symbol = null;
            return false;
        }
        if (_bySecurityId.TryGetValue(securityId, out var found))
        {
            symbol = found;
            return true;
        }
        symbol = null;
        return false;
    }

    /// <summary>
    /// Returns the per-instrument tick/lot constraints for a symbol if
    /// configured. Missing entries return false — fail-open is the
    /// caller's responsibility (used by the fat-finger checks so that
    /// an unconfigured symbol is not blocked by a non-existent tick).
    /// </summary>
    public bool TryGetSpec(string? symbol, out InstrumentSpec spec)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            spec = default;
            return false;
        }
        return _specs.TryGetValue(symbol, out spec!);
    }
}

/// <summary>
/// Per-instrument trading constraints that don't fit in
/// <see cref="Risk.RiskOptions"/> because they describe the
/// instrument itself, not the firm/end-client risk policy. Both
/// flat fields are optional — a null/missing value means "no
/// constraint", so partial entries are valid (e.g. a TickSize-only
/// spec for a symbol whose lot size is 1).
///
/// <para>
/// #360. <see cref="TickLadder"/> is an optional CVM-style tiered
/// tick schedule (price-band → tick) for venues where the tick
/// varies by price bucket. When present it overrides
/// <see cref="TickSize"/> on a per-price basis; <see cref="TickSize"/>
/// (when also set) acts as a fallback for prices below the lowest
/// band's <c>MinPriceInclusive</c> and as backwards-compatible
/// metadata. The ladder is canonicalized at construction time
/// (sorted ascending by <c>MinPriceInclusive</c>, malformed rows
/// dropped); see <see cref="ResolveTick(decimal)"/>.
/// </para>
/// </summary>
public readonly record struct InstrumentSpec(
    decimal? TickSize,
    long? LotSize,
    IReadOnlyList<TickBand>? TickLadder = null)
{
    /// <summary>
    /// #360. Returns the tick that applies at <paramref name="price"/>:
    /// the largest band whose <c>MinPriceInclusive &lt;= price</c>
    /// when <see cref="TickLadder"/> is set, otherwise the flat
    /// <see cref="TickSize"/>. Returns null when neither resolves
    /// (price below the lowest band and no flat fallback) — the
    /// caller treats that as fail-open consistent with the existing
    /// "unconfigured symbol" posture.
    /// </summary>
    public decimal? ResolveTick(decimal price)
    {
        if (TickLadder is { Count: > 0 } ladder)
        {
            decimal? match = null;
            // Linear scan is fine — production ladders are <10 rows.
            // Bands are sorted ascending so we keep the latest match.
            for (int i = 0; i < ladder.Count; i++)
            {
                if (ladder[i].MinPriceInclusive <= price) match = ladder[i].Tick;
                else break;
            }
            if (match.HasValue) return match;
        }
        return TickSize is > 0m ? TickSize : null;
    }

    /// <summary>
    /// #360. Returns the band index and tick that applied at
    /// <paramref name="price"/>, or null when no ladder is set or
    /// the price is below every band. Used by the reject reason in
    /// <c>MinTickSizeCheck</c> to surface the band range to the
    /// trader.
    /// </summary>
    public TickBandMatch? ResolveBand(decimal price)
    {
        if (TickLadder is not { Count: > 0 } ladder) return null;
        int idx = -1;
        for (int i = 0; i < ladder.Count; i++)
        {
            if (ladder[i].MinPriceInclusive <= price) idx = i;
            else break;
        }
        if (idx < 0) return null;
        var lo = ladder[idx].MinPriceInclusive;
        decimal? hi = idx + 1 < ladder.Count ? ladder[idx + 1].MinPriceInclusive : null;
        return new TickBandMatch(lo, hi, ladder[idx].Tick);
    }
}

/// <summary>
/// #360. One row of the CVM-style tiered tick ladder: the tick that
/// applies from <see cref="MinPriceInclusive"/> up to the next
/// band's <c>MinPriceInclusive</c> (exclusive) or +infinity.
/// </summary>
public readonly record struct TickBand(decimal MinPriceInclusive, decimal Tick);

/// <summary>
/// #360. Result of <see cref="InstrumentSpec.ResolveBand(decimal)"/>
/// — the half-open price range that produced the resolved tick.
/// <see cref="UpperExclusive"/> is null on the topmost band.
/// </summary>
public readonly record struct TickBandMatch(
    decimal LowerInclusive,
    decimal? UpperExclusive,
    decimal Tick);

/// <summary>
/// Bound from <c>Trading:SymbolDirectory</c>.
/// </summary>
public sealed class SymbolDirectoryOptions
{
    public const string SectionName = "Trading:SymbolDirectory";

    /// <summary>
    /// Symbol → SecurityId. Symbols with a zero or empty SecurityId
    /// are dropped at construction time (see <see cref="SymbolDirectory"/>).
    /// </summary>
    public Dictionary<string, ulong> SecurityIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Symbol → tick/lot constraints. Independent from
    /// <see cref="SecurityIds"/>: a symbol can appear in one without
    /// the other (e.g. the SecurityId is required for routing but
    /// tick/lot are operator-supplied for the fat-finger checks).
    /// </summary>
    public Dictionary<string, InstrumentSpecOptions> Specs { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Mutable counterpart of <see cref="InstrumentSpec"/> used only by
/// <c>Microsoft.Extensions.Configuration</c> binding (records with
/// non-null parameters don't bind well from JSON sections).
/// </summary>
public sealed class InstrumentSpecOptions
{
    public decimal? TickSize { get; set; }
    public long? LotSize { get; set; }

    /// <summary>
    /// #360. Optional CVM-style tiered tick schedule. Bound from
    /// <c>Trading:SymbolDirectory:Specs:&lt;SYMBOL&gt;:TickLadder</c>.
    /// Order in JSON does not matter — <see cref="SymbolDirectory"/>
    /// canonicalizes (sort + dedup + drop malformed) at startup.
    /// </summary>
    public List<TickBandOptions>? TickLadder { get; set; }
}

/// <summary>
/// #360. Mutable counterpart of <see cref="TickBand"/> for
/// configuration binding.
/// </summary>
public sealed class TickBandOptions
{
    public decimal MinPriceInclusive { get; set; }
    public decimal Tick { get; set; }
}
