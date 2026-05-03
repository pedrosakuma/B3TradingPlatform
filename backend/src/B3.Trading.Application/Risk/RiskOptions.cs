namespace B3.Trading.Application.Risk;

/// <summary>
/// Risk configuration. Bound from <c>Trading:Risk</c>. Resolution order
/// when computing limits for an order: per-end-client → per-firm →
/// per-symbol → default. First non-null wins per field.
/// </summary>
public sealed class RiskOptions
{
    public const string SectionName = "Trading:Risk";

    public RiskLimits Default { get; set; } = new();
    public Dictionary<string, RiskLimits> PerEndClient { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RiskLimits> PerFirm { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RiskLimits> PerSymbol { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> ReferencePrices { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public MarginOptions Margin { get; set; } = new();
}

/// <summary>
/// Reserve-on-submit margin configuration. Disabled by default —
/// <see cref="NoOpMarginProvider"/> stays in place until an operator
/// opts in. See <c>docs/rfcs/pre-trade-risk-v2.md</c> §3.1 for the
/// model assumed by v2 (crypto-spot ledger; not T+N, not derivatives).
/// </summary>
public sealed class MarginOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-end-client opening balance (in the single accounting
    /// currency v2 assumes). Missing entries are treated as zero, so
    /// unrecognized end-clients cannot place buy orders when margin
    /// is enabled — fail-closed by default.
    /// </summary>
    public Dictionary<string, decimal> Initial { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RiskLimits
{
    public long? MaxQuantity { get; set; }
    public decimal? MaxNotional { get; set; }
    public decimal? PriceCollarPercent { get; set; }
    public long? PositionLimit { get; set; }
}

public static class RiskLimitsResolver
{
    /// <summary>
    /// Resolves a single risk-limit field by walking the precedence
    /// chain <c>per-end-client → per-firm → per-symbol → default</c>
    /// and returning the first non-null value. <paramref name="firmId"/>
    /// may be null/blank when the caller has no firm context (legacy
    /// callers); the per-firm slot is then skipped.
    /// </summary>
    public static T? Resolve<T>(
        RiskOptions opts,
        string endClient,
        string? firmId,
        string symbol,
        Func<RiskLimits, T?> selector)
        where T : struct
    {
        if (opts.PerEndClient.TryGetValue(endClient, out var ec) && selector(ec).HasValue)
            return selector(ec);
        if (!string.IsNullOrWhiteSpace(firmId)
            && opts.PerFirm.TryGetValue(firmId, out var fi)
            && selector(fi).HasValue)
            return selector(fi);
        if (opts.PerSymbol.TryGetValue(symbol, out var sy) && selector(sy).HasValue)
            return selector(sy);
        return selector(opts.Default);
    }

    /// <summary>
    /// Convenience: resolve every <see cref="RiskLimits"/> field in
    /// one pass. Used by <c>GET /admin/risk/limits</c> to surface
    /// what the system actually thinks the cap is for a given
    /// (endClient, firm, symbol) tuple.
    /// </summary>
    public static RiskLimits ResolveAll(
        RiskOptions opts, string endClient, string? firmId, string symbol) =>
        new()
        {
            MaxQuantity = Resolve(opts, endClient, firmId, symbol, l => l.MaxQuantity),
            MaxNotional = Resolve(opts, endClient, firmId, symbol, l => l.MaxNotional),
            PriceCollarPercent = Resolve(opts, endClient, firmId, symbol, l => l.PriceCollarPercent),
            PositionLimit = Resolve(opts, endClient, firmId, symbol, l => l.PositionLimit),
        };
}
