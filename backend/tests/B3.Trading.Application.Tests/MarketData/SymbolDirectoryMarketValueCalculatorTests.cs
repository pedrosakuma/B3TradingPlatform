using B3.Trading.Application;
using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

public class SymbolDirectoryMarketValueCalculatorTests
{
    private static SymbolDirectory Dir(Action<SymbolDirectoryOptions> configure)
    {
        var opts = new SymbolDirectoryOptions();
        configure(opts);
        return new SymbolDirectory(opts);
    }

    [Fact]
    public void Equity_returnsPriceTimesQty()
    {
        var dir = Dir(o => o.Specs["PETR4"] = new InstrumentSpecOptions { TickSize = 0.01m, LotSize = 100 });
        var sut = new SymbolDirectoryMarketValueCalculator(dir);

        Assert.Equal(30_000m, sut.GetNotional("PETR4", price: 30m, quantity: 1_000));
    }

    [Fact]
    public void Option_appliesContractMultiplier()
    {
        // PETRL200 Call, multiplier 100, quoted at 0.50 per contract.
        // 10 contracts = 500 BRL of premium (10 * 0.50 * 100), NOT 5 BRL.
        // This is the compliance bug OPT-B closes: without the
        // multiplier, MaxNotional caps options at 0.5% of intended.
        var dir = Dir(o => o.Specs["PETRL200"] = new InstrumentSpecOptions
        {
            Option = new OptionMetadataOptions
            {
                ExpirationDate = new DateOnly(2026, 12, 18),
                PutOrCall = "Call",
                ExerciseStyle = "American",
                ContractMultiplier = 100m,
            },
        });
        var sut = new SymbolDirectoryMarketValueCalculator(dir);

        Assert.Equal(500m, sut.GetNotional("PETRL200", price: 0.50m, quantity: 10));
    }

    [Fact]
    public void Option_nonStandardMultiplier_isHonored()
    {
        // Index options on B3 often use multiplier = 1 (quoted in
        // index points); some mini-contracts use 5. The provider
        // must respect whatever the spec carries, not hardcode 100.
        var dir = Dir(o => o.Specs["MINI"] = new InstrumentSpecOptions
        {
            Option = new OptionMetadataOptions
            {
                ExpirationDate = new DateOnly(2026, 12, 18),
                PutOrCall = "Call",
                ExerciseStyle = "European",
                ContractMultiplier = 5m,
            },
        });
        var sut = new SymbolDirectoryMarketValueCalculator(dir);

        Assert.Equal(50m, sut.GetNotional("MINI", price: 2m, quantity: 5));
    }

    [Fact]
    public void UnknownSymbol_failsOpenAsEquity()
    {
        // Fail-open contract — symbols not configured fall back to
        // the equity formula so a missing directory entry never
        // makes a notional gate fire spurious rejects. Same posture
        // as ITickSizeProvider.
        var dir = Dir(_ => { });
        var sut = new SymbolDirectoryMarketValueCalculator(dir);

        Assert.Equal(3_000m, sut.GetNotional("UNKNOWN", price: 30m, quantity: 100));
    }

    [Fact]
    public void EquitySpecWithNoOptionBlock_returnsPriceTimesQty()
    {
        // Sanity: a richly-configured equity spec (tick + lot +
        // ladder) still yields the historical multiplier=1.
        var dir = Dir(o => o.Specs["VALE3"] = new InstrumentSpecOptions
        {
            TickSize = 0.01m,
            LotSize = 100,
            TickLadder = new()
            {
                new TickBandOptions { MinPriceInclusive = 0m, Tick = 0.01m },
                new TickBandOptions { MinPriceInclusive = 100m, Tick = 0.05m },
            },
        });
        var sut = new SymbolDirectoryMarketValueCalculator(dir);

        Assert.Equal(70_000m, sut.GetNotional("VALE3", price: 70m, quantity: 1_000));
    }

    [Fact]
    public void Constructor_nullDirectory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SymbolDirectoryMarketValueCalculator(null!));
    }

    [Fact]
    public void EquityFallbackInstance_returnsPriceTimesQty_withoutDirectory()
    {
        // Surfaced singleton used by risk-check ctors when DI hasn't
        // registered an IMarketValueCalculator — preserves the
        // pre-OPT-B equity behavior for test fixtures and legacy
        // wiring.
        Assert.Equal(900m, EquityMarketValueCalculator.Instance.GetNotional("X", 9m, 100));
        Assert.Same(EquityMarketValueCalculator.Instance, EquityMarketValueCalculator.Instance);
    }
}
