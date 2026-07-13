namespace B3.Trading.Application.MarketData;

/// <summary>
/// OPT-B (#484). Default <see cref="IMarketValueCalculator"/> impl
/// that reads <see cref="InstrumentSpec.Option"/> from the
/// configured <see cref="SymbolDirectory"/>. Symbols without an
/// option block (i.e. equity, or unknown) get the equity formula
/// (<c>multiplier = 1</c>); option symbols multiply by
/// <see cref="OptionMetadata.ContractMultiplier"/>.
///
/// <para>
/// Pairs with <see cref="SymbolDirectoryTickSizeProvider"/> — same
/// source of truth, same fail-open posture, registered side-by-side
/// in DI. Replaced wholesale by the SDK-driven directory projection
/// once #454 Fase 2 / pedrosakuma/B3MarketDataPlatform#55 ships.
/// </para>
/// </summary>
public sealed class SymbolDirectoryMarketValueCalculator : IMarketValueCalculator
{
    private readonly SymbolDirectory _directory;

    public SymbolDirectoryMarketValueCalculator(SymbolDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public decimal GetNotional(string symbol, decimal price, long quantity)
    {
        var multiplier = 1m;
        if (_directory.TryGetSpec(symbol, out var spec) && spec.Option is { } opt)
            multiplier = opt.ContractMultiplier;
        return price * quantity * multiplier;
    }
}
