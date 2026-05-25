using B3.Trading.Application;
using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

public class SymbolDirectoryTickSizeProviderTests
{
    private static SymbolDirectory Dir(Action<SymbolDirectoryOptions> configure)
    {
        var opts = new SymbolDirectoryOptions();
        configure(opts);
        return new SymbolDirectory(opts);
    }

    [Fact]
    public void FlatTick_Resolves_RegardlessOfReferencePrice()
    {
        var dir = Dir(o => o.Specs["PETR4"] = new InstrumentSpecOptions { TickSize = 0.01m });
        var sut = new SymbolDirectoryTickSizeProvider(dir);

        Assert.True(sut.TryGetTickSize("PETR4", referencePrice: 30m, out var t1));
        Assert.Equal(0.01m, t1);

        Assert.True(sut.TryGetTickSize("PETR4", referencePrice: null, out var t2));
        Assert.Equal(0.01m, t2);
    }

    [Fact]
    public void Ladder_Resolves_BandSpecificTick_WhenReferencePriceProvided()
    {
        var dir = Dir(o => o.Specs["XPTO"] = new InstrumentSpecOptions
        {
            TickSize = 0.01m, // flat fallback
            TickLadder = new()
            {
                new TickBandOptions { MinPriceInclusive = 0m,   Tick = 0.01m },
                new TickBandOptions { MinPriceInclusive = 10m,  Tick = 0.05m },
                new TickBandOptions { MinPriceInclusive = 100m, Tick = 0.10m },
            }
        });
        var sut = new SymbolDirectoryTickSizeProvider(dir);

        Assert.True(sut.TryGetTickSize("XPTO", 5m, out var t1));
        Assert.Equal(0.01m, t1);

        Assert.True(sut.TryGetTickSize("XPTO", 50m, out var t2));
        Assert.Equal(0.05m, t2);

        Assert.True(sut.TryGetTickSize("XPTO", 500m, out var t3));
        Assert.Equal(0.10m, t3);
    }

    [Fact]
    public void Ladder_WithoutReferencePrice_FallsBackToFlatTick()
    {
        var dir = Dir(o => o.Specs["XPTO"] = new InstrumentSpecOptions
        {
            TickSize = 0.07m,
            TickLadder = new()
            {
                new TickBandOptions { MinPriceInclusive = 0m, Tick = 0.05m },
            }
        });
        var sut = new SymbolDirectoryTickSizeProvider(dir);

        Assert.True(sut.TryGetTickSize("XPTO", referencePrice: null, out var tick));
        Assert.Equal(0.07m, tick);
    }

    [Fact]
    public void LadderOnly_WithoutReferencePrice_ReturnsFalse()
    {
        var dir = Dir(o => o.Specs["XPTO"] = new InstrumentSpecOptions
        {
            TickSize = null,
            TickLadder = new()
            {
                new TickBandOptions { MinPriceInclusive = 0m, Tick = 0.05m },
            }
        });
        var sut = new SymbolDirectoryTickSizeProvider(dir);

        Assert.False(sut.TryGetTickSize("XPTO", referencePrice: null, out var tick));
        Assert.Equal(0m, tick);
    }

    [Fact]
    public void UnknownSymbol_ReturnsFalse()
    {
        var dir = Dir(o => o.Specs["PETR4"] = new InstrumentSpecOptions { TickSize = 0.01m });
        var sut = new SymbolDirectoryTickSizeProvider(dir);

        Assert.False(sut.TryGetTickSize("DOESNOTEXIST", 10m, out var tick));
        Assert.Equal(0m, tick);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespaceSymbol_ReturnsFalse(string? symbol)
    {
        var dir = Dir(_ => { });
        var sut = new SymbolDirectoryTickSizeProvider(dir);

        Assert.False(sut.TryGetTickSize(symbol!, 10m, out var tick));
        Assert.Equal(0m, tick);
    }

    [Fact]
    public void ZeroOrNegativeFlatTick_ReturnsFalse()
    {
        var dir = Dir(o => o.Specs["PETR4"] = new InstrumentSpecOptions { TickSize = 0m });
        var sut = new SymbolDirectoryTickSizeProvider(dir);

        Assert.False(sut.TryGetTickSize("PETR4", referencePrice: null, out var tick));
        Assert.Equal(0m, tick);
    }

    [Fact]
    public void Ctor_NullDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SymbolDirectoryTickSizeProvider(null!));
    }
}
