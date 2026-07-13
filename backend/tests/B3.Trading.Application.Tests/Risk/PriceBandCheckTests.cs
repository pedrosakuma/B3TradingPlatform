using System.Diagnostics.Metrics;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// OPT-E (#487). Pre-trade gate semantics + metric tagging tests for
/// <see cref="PriceBandCheck"/>.
/// </summary>
public sealed class PriceBandCheckTests
{
    private static readonly DateTimeOffset BandAsOf = new(2026, 5, 26, 15, 0, 0, TimeSpan.Zero);

    private static RiskContext Ctx(
        decimal? price,
        OrderSide side = OrderSide.Buy,
        OrderType type = OrderType.Limit,
        string symbol = "PETR4") =>
        new(new EndClientId("alice"), "firm1", symbol, side, type, 100, price);

    private sealed class StubSource : IPriceBandSource
    {
        private readonly Dictionary<string, PriceBand> _bands = new(StringComparer.OrdinalIgnoreCase);
        public StubSource Set(string symbol, decimal lower, decimal upper, DateTimeOffset asOf)
        {
            _bands[symbol] = new PriceBand(lower, upper, asOf);
            return this;
        }
        public bool TryGetBand(string? symbol, out PriceBand band)
        {
            if (symbol is null) { band = default; return false; }
            return _bands.TryGetValue(symbol, out band);
        }
    }

    [Fact]
    public void MarketOrder_NoPrice_Approved()
    {
        var check = new PriceBandCheck(new StubSource(), TimeProvider.System);
        var d = check.Check(Ctx(price: null, type: OrderType.Market));
        Assert.True(d.Approved);
    }

    [Fact]
    public void NoBand_FailsOpen_BumpsBypassCounter()
    {
        var check = new PriceBandCheck(NullPriceBandSource.Instance, TimeProvider.System);

        long bypass = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "B3.Trading"
                && inst.Name == MetricsRegistry.PriceBandBypassedNoBand.Name)
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, m, tags, _) =>
        {
            foreach (var kv in tags.ToArray())
                if (kv.Key == "symbol" && (string?)kv.Value == "PETR4")
                    Interlocked.Add(ref bypass, m);
        });
        listener.Start();

        var d = check.Check(Ctx(price: 25m));
        Assert.True(d.Approved);
        Assert.True(Interlocked.Read(ref bypass) >= 1,
            $"bypass counter should fire for unknown symbol, got {bypass}");
    }

    [Fact]
    public void WithinBand_Approved()
    {
        var src = new StubSource().Set("PETR4", 24m, 26m, BandAsOf);
        var check = new PriceBandCheck(src, TimeProvider.System);

        Assert.True(check.Check(Ctx(price: 24m)).Approved);
        Assert.True(check.Check(Ctx(price: 25m)).Approved);
        Assert.True(check.Check(Ctx(price: 26m)).Approved);
    }

    [Fact]
    public void BelowBand_Rejected_WithStableCode()
    {
        var src = new StubSource().Set("PETR4", 24m, 26m, BandAsOf);
        var check = new PriceBandCheck(src, TimeProvider.System);

        var d = check.Check(Ctx(price: 23m));
        Assert.False(d.Approved);
        Assert.Equal(RiskRejectCodes.PriceBand, d.Code);
        Assert.Contains("below", d.Reason);
    }

    [Fact]
    public void AboveBand_Rejected_WithStableCode()
    {
        var src = new StubSource().Set("PETR4", 24m, 26m, BandAsOf);
        var check = new PriceBandCheck(src, TimeProvider.System);

        var d = check.Check(Ctx(price: 27m));
        Assert.False(d.Approved);
        Assert.Equal(RiskRejectCodes.PriceBand, d.Code);
        Assert.Contains("above", d.Reason);
    }

    [Fact]
    public void Reject_EmitsCounterWithSymbolSideReasonTags()
    {
        var src = new StubSource().Set("PETR4", 24m, 26m, BandAsOf);
        var check = new PriceBandCheck(src, TimeProvider.System);

        var tags = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        using var listener = ListenCounter<long>(MetricsRegistry.PriceBandRejects.Name, tags);

        check.Check(Ctx(price: 23m, side: OrderSide.Sell));

        var matching = tags.Where(t =>
            t.Any(kv => kv.Key == "symbol" && (string?)kv.Value == "PETR4")
            && t.Any(kv => kv.Key == "side" && (string?)kv.Value == "Sell")
            && t.Any(kv => kv.Key == "reason" && (string?)kv.Value == "below"))
            .ToList();

        Assert.NotEmpty(matching);
    }

    [Fact]
    public void Consult_RecordsAgeHistogram_FromTimeProvider()
    {
        var src = new StubSource().Set("PETR4", 24m, 26m, BandAsOf);
        var fake = new FixedTimeProvider(BandAsOf.AddSeconds(7));
        var check = new PriceBandCheck(src, fake);

        var values = new List<double>();
        var symbols = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "B3.Trading" && inst.Name == MetricsRegistry.PriceBandAgeSeconds.Name)
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<double>((inst, m, t, _) =>
        {
            values.Add(m);
            foreach (var kv in t.ToArray())
                if (kv.Key == "symbol") symbols.Add(kv.Value as string);
        });
        listener.Start();

        check.Check(Ctx(price: 25m));

        Assert.Contains(values, v => Math.Abs(v - 7d) < 0.01);
        Assert.Contains("PETR4", symbols);
    }

    [Fact]
    public void Consult_ClockBeforeBand_RecordsZeroAge_NotNegative()
    {
        // Defensive: a clock skew (test clock behind band timestamp)
        // must not surface as a negative histogram observation.
        var src = new StubSource().Set("PETR4", 24m, 26m, BandAsOf);
        var fake = new FixedTimeProvider(BandAsOf.AddSeconds(-10));
        var check = new PriceBandCheck(src, fake);

        var values = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "B3.Trading" && inst.Name == MetricsRegistry.PriceBandAgeSeconds.Name)
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<double>((_, m, _, _) => values.Add(m));
        listener.Start();

        check.Check(Ctx(price: 25m));

        Assert.All(values, v => Assert.True(v >= 0d, $"age must be clamped to >=0, got {v}"));
    }

    [Fact]
    public void CheckOrder_RunsAfterPriceCollar()
    {
        var check = new PriceBandCheck(NullPriceBandSource.Instance);
        // PriceCollarCheck.Order = 300 ; PriceBandCheck must run right
        // after so a sustained band/collar disagreement is observable
        // (collar reject vs band reject in two distinct RiskRejectCodes).
        Assert.True(check.Order > 300);
        Assert.Equal("price_band", check.Name);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static MeterListener ListenCounter<T>(string name, List<IReadOnlyList<KeyValuePair<string, object?>>> sink) where T : struct
    {
        var l = new MeterListener();
        l.InstrumentPublished = (inst, lst) =>
        {
            if (inst.Meter.Name == "B3.Trading" && inst.Name == name)
                lst.EnableMeasurementEvents(inst);
        };
        l.SetMeasurementEventCallback<T>((_, _, tags, _) =>
        {
            lock (sink) sink.Add(tags.ToArray().ToList());
        });
        l.Start();
        return l;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
