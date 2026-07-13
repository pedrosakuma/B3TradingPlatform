using B3.Trading.Application;
using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// OPT-D (#486, refs #454 Fase 2). The registry is the projection
/// target for SDK 0.5.0's <c>SecurityDefinition</c> WebSocket
/// channel. These tests pin the upsert + lookup contract; the
/// SDK→registry translation lives in
/// <c>B3.Trading.Host.MarketData.SdkMarketDataSubscriber</c> and is
/// covered separately.
/// </summary>
public class SecurityDefinitionRegistryTests
{
    [Fact]
    public void TryGetSpec_ReturnsFalse_WhenSymbolMissing()
    {
        var sut = new SecurityDefinitionRegistry();
        Assert.False(sut.TryGetSpec("PETR4", out var spec));
        Assert.Equal(default, spec);
    }

    [Fact]
    public void TryGetSpec_ReturnsFalse_OnNullOrWhitespace()
    {
        var sut = new SecurityDefinitionRegistry();
        sut.Upsert("PETR4", new InstrumentSpec(TickSize: 0.01m, LotSize: 100), 4);
        Assert.False(sut.TryGetSpec("", out _));
        Assert.False(sut.TryGetSpec("   ", out _));
        Assert.False(sut.TryGetSpec(null, out _));
    }

    [Fact]
    public void Upsert_StoresSpec_AndIsCaseInsensitive()
    {
        var sut = new SecurityDefinitionRegistry();
        var spec = new InstrumentSpec(TickSize: 0.05m, LotSize: 10);
        sut.Upsert("PETR4", spec, 4);

        // Round-trip exact match
        Assert.True(sut.TryGetSpec("PETR4", out var got));
        Assert.Equal(0.05m, got.TickSize);
        Assert.Equal(10, got.LotSize);

        // Case-insensitive lookup — matches SymbolDirectory contract
        Assert.True(sut.TryGetSpec("petr4", out var lower));
        Assert.Equal(spec, lower);
        Assert.True(sut.TryGetSpec("Petr4", out var mixed));
        Assert.Equal(spec, mixed);
    }

    [Fact]
    public void Upsert_ReplacesPreviousSpec_LastWriteWins()
    {
        var sut = new SecurityDefinitionRegistry();
        sut.Upsert("PETR4", new InstrumentSpec(TickSize: 0.01m, LotSize: 100), 4);
        sut.Upsert("PETR4", new InstrumentSpec(TickSize: 0.02m, LotSize: 50), 4);

        Assert.True(sut.TryGetSpec("PETR4", out var spec));
        Assert.Equal(0.02m, spec.TickSize);
        Assert.Equal(50, spec.LotSize);
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public void Upsert_IsNoOp_WhenSymbolIsBlank()
    {
        var sut = new SecurityDefinitionRegistry();
        sut.Upsert("", new InstrumentSpec(0.01m, 100), 0);
        sut.Upsert("   ", new InstrumentSpec(0.01m, 100), 0);
        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public void TryGetSecurityId_RoundTrips_AndIsCaseInsensitive()
    {
        var sut = new SecurityDefinitionRegistry();
        sut.Upsert("PETR4", new InstrumentSpec(0.01m, 100), 4);
        Assert.True(sut.TryGetSecurityId("petr4", out var id));
        Assert.Equal(4UL, id);

        Assert.False(sut.TryGetSecurityId("UNKNOWN", out var unknown));
        Assert.Equal(0UL, unknown);
        Assert.False(sut.TryGetSecurityId("", out _));
    }

    [Fact]
    public void Upsert_IsThreadSafe_UnderConcurrentWriters()
    {
        // SDK callback path is single-threaded but registry's
        // contract allows concurrent writers per the doc; this test
        // pins that invariant so a future SDK change (or a parallel
        // bootstrap path) doesn't silently corrupt the dictionary.
        var sut = new SecurityDefinitionRegistry();
        Parallel.For(0, 200, i =>
        {
            var sym = "SYM" + (i % 20);
            sut.Upsert(sym, new InstrumentSpec(0.01m * (i % 20 + 1), i + 1), (ulong)i);
        });

        Assert.Equal(20, sut.Count);
        for (int i = 0; i < 20; i++)
        {
            Assert.True(sut.TryGetSpec("SYM" + i, out var spec));
            Assert.True(spec.TickSize > 0);
        }
    }
}
