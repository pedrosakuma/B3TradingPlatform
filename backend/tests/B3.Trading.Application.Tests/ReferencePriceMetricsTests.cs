using System.Diagnostics.Metrics;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Slice 5 wrap-up: verifies the observability hooks added to the
/// reference-price path. The collar's behaviour is unchanged — these
/// tests only assert that lookups are tagged with the correct source
/// and that bypass-due-to-no-reference is counted, which is what ops
/// will use to alert on a degraded MD feed.
/// </summary>
public class ReferencePriceMetricsTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly object _gate = new();
    private readonly List<(string Instrument, long Value, IDictionary<string, object?> Tags)> _samples = new();

    public ReferencePriceMetricsTests()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                // Only enable the two counters this fixture cares about.
                // Other B3.Trading instruments would otherwise bleed in
                // from xunit parallelism (other tests sharing the meter).
                if (inst.Meter.Name == "B3.Trading" &&
                    (inst.Name == "trading.risk.refprice.lookups" ||
                     inst.Name == "trading.risk.collar.bypassed_no_reference"))
                {
                    l.EnableMeasurementEvents(inst);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((inst, v, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            lock (_gate) _samples.Add((inst.Name, v, dict));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private List<(string Instrument, long Value, IDictionary<string, object?> Tags)> Snapshot()
    {
        lock (_gate) return _samples.ToList();
    }

    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) => new StaticOptionsMonitor<RiskOptions>(o);

    private static RiskContext Ctx(string symbol = "PETR4", decimal? price = 30m) =>
        new(new EndClientId("alice"), "FIRM", symbol, OrderSide.Buy, OrderType.Limit, 100, price);

    private static string? Tag(IDictionary<string, object?> tags, string key) =>
        tags.TryGetValue(key, out var v) ? v as string : null;

    [Fact]
    public void Collar_LookupTaggedAsLive_WhenLiveCacheHitsFresh()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = BuildLiveRef(sub, new StaticFallback(), clock);
        sub.RaiseTrade("PETR4", 30m, clock.GetUtcNow());

        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PriceCollarPercent = 10m } });
        new PriceCollarCheck(opts, rp).Check(Ctx(price: 30m));

        Assert.Contains(Snapshot(), s =>
            s.Instrument == "trading.risk.refprice.lookups" &&
            Tag(s.Tags, "source") == "live" &&
            Tag(s.Tags, "symbol") == "PETR4");
    }

    [Fact]
    public void Collar_LookupTaggedAsFallback_WhenLiveMissesAndConfigHits()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = BuildLiveRef(sub, new StaticFallback(("PETR4", 30m)), clock);
        // No trade raised → live cache empty, falls through to static.

        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PriceCollarPercent = 10m } });
        new PriceCollarCheck(opts, rp).Check(Ctx(price: 30m));

        Assert.Contains(Snapshot(), s =>
            s.Instrument == "trading.risk.refprice.lookups" &&
            Tag(s.Tags, "source") == "fallback");
    }

    [Fact]
    public void Collar_LookupTaggedAsMissing_AndBypassCounterFires_WhenNoReferenceAtAll()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = BuildLiveRef(sub, new StaticFallback(), clock);

        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PriceCollarPercent = 10m } });
        new PriceCollarCheck(opts, rp).Check(Ctx(symbol: "VALE3", price: 30m));

        Assert.Contains(Snapshot(), s =>
            s.Instrument == "trading.risk.refprice.lookups" &&
            Tag(s.Tags, "source") == "missing");
        Assert.Contains(Snapshot(), s =>
            s.Instrument == "trading.risk.collar.bypassed_no_reference" &&
            Tag(s.Tags, "symbol") == "VALE3");
    }

    [Fact]
    public void Collar_NoBypassCounter_WhenReferenceFoundAndOrderRejected()
    {
        var refPx = new StaticFallback(("PETR4", 30m));
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PriceCollarPercent = 1m } });
        var d = new PriceCollarCheck(opts, refPx).Check(Ctx(price: 35m));
        Assert.False(d.Approved);
        Assert.DoesNotContain(Snapshot(), s =>
            s.Instrument == "trading.risk.collar.bypassed_no_reference");
    }

    [Fact]
    public void Collar_NoBypassCounter_WhenCollarNotConfigured()
    {
        // No PriceCollarPercent on the resolved limits → check returns
        // before doing any lookup; no metric should fire.
        var opts = Wrap(new RiskOptions());
        new PriceCollarCheck(opts, new StaticFallback()).Check(Ctx(price: 100m));
        Assert.Empty(Snapshot());
    }

    [Fact]
    public void ConfigReferencePrice_LookupReportsFallbackSource()
    {
        var monitor = new StaticOptionsMonitor<RiskOptions>(
            new RiskOptions { ReferencePrices = { ["PETR4"] = 30m } });
        var rp = new ConfigReferencePrice(monitor);

        var hit = rp.Lookup("PETR4");
        Assert.True(hit.Found);
        Assert.Equal(ReferencePriceSource.Fallback, hit.Source);
        Assert.Equal(30m, hit.Price);

        var miss = rp.Lookup("VALE3");
        Assert.False(miss.Found);
        Assert.Equal(ReferencePriceSource.Missing, miss.Source);
    }

    [Fact]
    public void MarketDataReferencePrice_LookupReportsLiveAndFallbackAndMissing()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = BuildLiveRef(sub, new StaticFallback(("VALE3", 60m)), clock,
            maxStaleness: TimeSpan.FromSeconds(10));
        sub.RaiseTrade("PETR4", 28.5m, clock.GetUtcNow());

        Assert.Equal(ReferencePriceSource.Live, rp.Lookup("PETR4").Source);
        Assert.Equal(ReferencePriceSource.Fallback, rp.Lookup("VALE3").Source);
        Assert.Equal(ReferencePriceSource.Missing, rp.Lookup("ITUB4").Source);

        // Stale entry should also fall back, not stay live.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ReferencePriceSource.Missing, rp.Lookup("PETR4").Source);
    }

    [Fact]
    public void StalenessGauge_PublishesPerSymbolAge()
    {
        var observed = new List<(string Symbol, double Seconds)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Name == "trading.risk.refprice.staleness_seconds")
                    l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<double>((inst, v, tags, _) =>
        {
            string? symbol = null;
            foreach (var t in tags) if (t.Key == "symbol") symbol = (string?)t.Value;
            if (symbol != null) observed.Add((symbol, v));
        });
        listener.Start();

        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = BuildLiveRef(sub, new StaticFallback(), clock);
        sub.RaiseTrade("PETR4", 30m, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(7));

        listener.RecordObservableInstruments();

        Assert.Contains(observed, o => o.Symbol == "PETR4" && o.Seconds >= 7d);
    }

    private static MarketDataReferencePrice BuildLiveRef(
        FakeMarketDataSubscriber sub, IReferencePrice fallback, TestClock clock,
        TimeSpan? maxStaleness = null) =>
        new(sub, fallback,
            Options.Create(new MarketDataOptions
            {
                WsUrl = "ws://test",
                MaxStaleness = maxStaleness ?? TimeSpan.FromMinutes(5),
            }),
            clock,
            NullLogger<MarketDataReferencePrice>.Instance);
}
