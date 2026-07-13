using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// OPT-E (#487). Concurrency + lookup contract tests for
/// <see cref="PriceBandRegistry"/>. Mirrors the
/// <c>SecurityDefinitionRegistryTests</c> shape.
/// </summary>
public sealed class PriceBandRegistryTests
{
    private static DateTimeOffset T(int seconds) =>
        new(2026, 5, 26, 15, 0, seconds, TimeSpan.Zero);

    [Fact]
    public void Upsert_ThenTryGetBand_RoundTrips()
    {
        var reg = new PriceBandRegistry();
        reg.Upsert("PETR4", lower: 24.50m, upper: 26.75m, asOfUtc: T(0));

        Assert.True(reg.TryGetBand("PETR4", out var band));
        Assert.Equal(24.50m, band.Lower);
        Assert.Equal(26.75m, band.Upper);
        Assert.Equal(T(0), band.AsOfUtc);
    }

    [Fact]
    public void TryGetBand_IsCaseInsensitive()
    {
        var reg = new PriceBandRegistry();
        reg.Upsert("PETR4", 10m, 12m, T(0));

        Assert.True(reg.TryGetBand("petr4", out _));
        Assert.True(reg.TryGetBand("Petr4", out _));
    }

    [Fact]
    public void Upsert_ReplacesPreviousBand_WholeRecord()
    {
        var reg = new PriceBandRegistry();
        reg.Upsert("PETR4", 10m, 12m, T(0));
        reg.Upsert("PETR4", 11m, 13m, T(5));

        Assert.True(reg.TryGetBand("PETR4", out var band));
        Assert.Equal(11m, band.Lower);
        Assert.Equal(13m, band.Upper);
        Assert.Equal(T(5), band.AsOfUtc);
    }

    [Fact]
    public void TryGetBand_UnknownSymbol_ReturnsFalse()
    {
        var reg = new PriceBandRegistry();
        Assert.False(reg.TryGetBand("ABCD3", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetBand_NullOrWhitespace_ReturnsFalse(string? symbol)
    {
        var reg = new PriceBandRegistry();
        reg.Upsert("PETR4", 10m, 12m, T(0));
        Assert.False(reg.TryGetBand(symbol, out _));
    }

    [Fact]
    public void Upsert_NullOrWhitespaceSymbol_IsNoOp()
    {
        var reg = new PriceBandRegistry();
        reg.Upsert("  ", 10m, 12m, T(0));
        Assert.Equal(0, reg.Count);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(12, 10)] // inverted
    public void Upsert_InvalidBounds_IsNoOp(decimal lb, decimal ub)
    {
        var reg = new PriceBandRegistry();
        reg.Upsert("PETR4", lb, ub, T(0));
        Assert.Equal(0, reg.Count);
        Assert.False(reg.TryGetBand("PETR4", out _));
    }

    [Fact]
    public async Task ConcurrentWritersAndReaders_NoTorn()
    {
        // The SDK gives us single-threaded writes in production but
        // we still exercise the multi-writer path so a future fan-out
        // (e.g. a second SDK channel projecting into the same registry)
        // doesn't silently corrupt the dictionary. Read side is what
        // matters for the risk hot path — must never observe a torn
        // record.
        var reg = new PriceBandRegistry();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            var rnd = new Random(w);
            while (!token.IsCancellationRequested)
            {
                var sym = $"SYM{rnd.Next(0, 16)}";
                var lo = (decimal)(rnd.NextDouble() * 10 + 1);
                var up = lo + (decimal)(rnd.NextDouble() * 5 + 0.1);
                reg.Upsert(sym, lo, up, T(rnd.Next(60)));
            }
        }, token)).ToArray();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                for (var i = 0; i < 16; i++)
                {
                    if (reg.TryGetBand($"SYM{i}", out var band))
                    {
                        // Invariant: every observed band must be
                        // internally consistent (lower <= upper, both
                        // positive). A torn read would surface here.
                        Assert.True(band.Lower > 0m);
                        Assert.True(band.Upper >= band.Lower);
                    }
                }
            }
        }, token)).ToArray();

        try { await Task.WhenAll(writers.Concat(readers)); }
        catch (OperationCanceledException) { /* expected */ }
    }

    [Fact]
    public void NullPriceBandSource_AlwaysReturnsFalse()
    {
        IPriceBandSource src = NullPriceBandSource.Instance;
        Assert.False(src.TryGetBand("PETR4", out var band));
        Assert.Equal(default, band);
    }
}
