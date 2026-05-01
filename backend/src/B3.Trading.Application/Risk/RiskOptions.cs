namespace B3.Trading.Application.Risk;

/// <summary>
/// Risk configuration. Bound from <c>Trading:Risk</c>. Resolution order
/// when computing limits for an order: per-end-client → per-symbol →
/// default. First non-null wins per field.
/// </summary>
public sealed class RiskOptions
{
    public const string SectionName = "Trading:Risk";

    public RiskLimits Default { get; set; } = new();
    public Dictionary<string, RiskLimits> PerEndClient { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RiskLimits> PerSymbol { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> ReferencePrices { get; set; } =
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
    public static T? Resolve<T>(RiskOptions opts, string endClient, string symbol, Func<RiskLimits, T?> selector)
        where T : struct
    {
        if (opts.PerEndClient.TryGetValue(endClient, out var ec) && selector(ec).HasValue)
            return selector(ec);
        if (opts.PerSymbol.TryGetValue(symbol, out var sy) && selector(sy).HasValue)
            return selector(sy);
        return selector(opts.Default);
    }
}
