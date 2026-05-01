namespace B3.Trading.Application.Risk;

/// <summary>
/// Reference price source for <see cref="Checks.PriceCollarCheck"/>. v1
/// reads from <see cref="RiskOptions.ReferencePrices"/>; future
/// implementations could subscribe to <c>B3MarketDataPlatform</c> last-trade
/// or top-of-book ticks.
/// </summary>
public interface IReferencePrice
{
    bool TryGet(string symbol, out decimal price);
}

public sealed class ConfigReferencePrice : IReferencePrice
{
    private readonly RiskOptions _options;
    public ConfigReferencePrice(Microsoft.Extensions.Options.IOptions<RiskOptions> options) =>
        _options = options.Value;

    public bool TryGet(string symbol, out decimal price) =>
        _options.ReferencePrices.TryGetValue(symbol, out price);
}
