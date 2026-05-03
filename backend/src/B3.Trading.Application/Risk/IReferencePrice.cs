namespace B3.Trading.Application.Risk;

/// <summary>
/// Where a reference-price reading came from. Surfaced as a metric tag
/// so ops can see whether the live MD feed is actually feeding the
/// collar or whether it has degraded to the static config table.
/// </summary>
public enum ReferencePriceSource
{
    /// <summary>Fresh sample from the live market-data cache.</summary>
    Live,
    /// <summary>The live cache missed (or was stale) and we fell back to the static config table.</summary>
    Fallback,
    /// <summary>No source had a number for the symbol.</summary>
    Missing,
}

/// <summary>
/// Outcome of a reference-price lookup. <see cref="Found"/> is true
/// for both Live and Fallback; only Missing returns false.
/// </summary>
public readonly record struct ReferencePriceLookup(decimal Price, ReferencePriceSource Source)
{
    public bool Found => Source != ReferencePriceSource.Missing;
    public static ReferencePriceLookup NotFound { get; } = new(0m, ReferencePriceSource.Missing);
}

/// <summary>
/// Reference price source for <see cref="Checks.PriceCollarCheck"/>.
/// Implementations: <see cref="ConfigReferencePrice"/> (static config
/// table) and
/// <see cref="MarketData.MarketDataReferencePrice"/> (live B3 MD feed
/// with config fallback).
/// </summary>
public interface IReferencePrice
{
    bool TryGet(string symbol, out decimal price);

    /// <summary>
    /// Same lookup as <see cref="TryGet"/> but reports the source of
    /// the reading (Live / Fallback / Missing) so the caller can emit
    /// observability tags. Default impl wraps <see cref="TryGet"/> and
    /// classifies hits as <see cref="ReferencePriceSource.Fallback"/>
    /// (static — the safe assumption for any implementation that
    /// hasn't opted into the richer contract).
    /// </summary>
    ReferencePriceLookup Lookup(string symbol)
    {
        if (TryGet(symbol, out var price))
            return new ReferencePriceLookup(price, ReferencePriceSource.Fallback);
        return ReferencePriceLookup.NotFound;
    }
}

public sealed class ConfigReferencePrice : IReferencePrice
{
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<RiskOptions> _options;
    public ConfigReferencePrice(Microsoft.Extensions.Options.IOptionsMonitor<RiskOptions> options) =>
        _options = options;

    public bool TryGet(string symbol, out decimal price) =>
        _options.CurrentValue.ReferencePrices.TryGetValue(symbol, out price);

    public ReferencePriceLookup Lookup(string symbol) =>
        TryGet(symbol, out var price)
            ? new ReferencePriceLookup(price, ReferencePriceSource.Fallback)
            : ReferencePriceLookup.NotFound;
}
