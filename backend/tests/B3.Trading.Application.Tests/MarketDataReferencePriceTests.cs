using B3.Trading.Application.MarketData;
using B3.Trading.Application.Risk;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace B3.Trading.Application.Tests;

public class MarketDataReferencePriceTests
{
    private static MarketDataReferencePrice Build(
        FakeMarketDataSubscriber sub,
        IReferencePrice fallback,
        TestClock clock,
        TimeSpan? maxStaleness = null,
        string[]? symbols = null)
    {
        var opts = Options.Create(new MarketDataOptions
        {
            WsUrl = "ws://test",
            Symbols = symbols ?? Array.Empty<string>(),
            MaxStaleness = maxStaleness ?? TimeSpan.FromMinutes(5),
        });
        return new MarketDataReferencePrice(
            sub, fallback, opts, clock,
            NullLogger<MarketDataReferencePrice>.Instance);
    }

    [Fact]
    public void Trade_seeds_cache_and_TryGet_returns_it()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock);

        sub.RaiseTrade("PETR4", 28.50m, clock.GetUtcNow());

        Assert.True(rp.TryGet("PETR4", out var px));
        Assert.Equal(28.50m, px);
    }

    [Fact]
    public void TryGet_is_case_insensitive()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock);

        sub.RaiseTrade("PETR4", 28.50m, clock.GetUtcNow());

        Assert.True(rp.TryGet("petr4", out var px));
        Assert.Equal(28.50m, px);
    }

    [Fact]
    public void Cache_miss_falls_back_to_inner_reference_price()
    {
        var sub = new FakeMarketDataSubscriber();
        var fallback = new StaticFallback(("VALE3", 60m));
        var rp = Build(sub, fallback, new TestClock(DateTimeOffset.UtcNow));

        Assert.True(rp.TryGet("VALE3", out var px));
        Assert.Equal(60m, px);
    }

    [Fact]
    public void Stale_cache_entry_falls_back_to_inner_reference_price()
    {
        var sub = new FakeMarketDataSubscriber();
        var fallback = new StaticFallback(("PETR4", 25m));
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, fallback, clock, maxStaleness: TimeSpan.FromSeconds(10));

        sub.RaiseTrade("PETR4", 28.50m, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1)); // > 10s staleness

        Assert.True(rp.TryGet("PETR4", out var px));
        Assert.Equal(25m, px);
    }

    [Fact]
    public void Negative_or_zero_price_is_ignored()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock);

        sub.RaiseTrade("PETR4", 0m, clock.GetUtcNow());
        sub.RaiseTrade("PETR4", -1m, clock.GetUtcNow());

        Assert.False(rp.TryGet("PETR4", out _));
    }

    [Fact]
    public void Blank_symbol_is_ignored()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock);

        sub.RaiseTrade("   ", 28.50m, clock.GetUtcNow());

        Assert.False(rp.TryGet("   ", out _));
        Assert.Empty(rp.Snapshot());
    }

    [Fact]
    public void InfoSnapshot_seeds_with_LastTradePrice_when_present()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock);

        sub.RaiseInfo(new MarketInfoSnapshot(
            Symbol: "PETR4", SecurityId: 0,
            LastTradePrice: 30m, TradingReferencePrice: 25m,
            ReceivedUtc: clock.GetUtcNow()));

        Assert.True(rp.TryGet("PETR4", out var px));
        Assert.Equal(30m, px); // LastTradePrice wins over TradingReferencePrice
    }

    [Fact]
    public void InfoSnapshot_falls_back_to_TradingReferencePrice_when_no_last_trade()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock);

        sub.RaiseInfo(new MarketInfoSnapshot(
            Symbol: "PETR4", SecurityId: 0,
            LastTradePrice: null, TradingReferencePrice: 25m,
            ReceivedUtc: clock.GetUtcNow()));

        Assert.True(rp.TryGet("PETR4", out var px));
        Assert.Equal(25m, px);
    }

    [Fact]
    public void InfoSnapshot_with_no_usable_price_does_not_seed_cache()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock);

        sub.RaiseInfo(new MarketInfoSnapshot(
            Symbol: "PETR4", SecurityId: 0,
            LastTradePrice: null, TradingReferencePrice: null,
            ReceivedUtc: clock.GetUtcNow()));

        Assert.False(rp.TryGet("PETR4", out _));
    }

    [Fact]
    public async Task StartAsync_subscribes_each_configured_symbol()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock,
            symbols: new[] { "PETR4", "VALE3" });

        await rp.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { "PETR4", "VALE3" }, sub.Subscriptions.ToArray());
        Assert.True(sub.ConnectCalled);
    }

    [Fact]
    public async Task StartAsync_with_empty_symbols_still_connects_but_subscribes_nothing()
    {
        var sub = new FakeMarketDataSubscriber();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var rp = Build(sub, new StaticFallback(), clock, symbols: Array.Empty<string>());

        await rp.StartAsync(CancellationToken.None);

        Assert.True(sub.ConnectCalled);
        Assert.Empty(sub.Subscriptions);
    }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;
    public TestClock(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

internal sealed class StaticFallback : IReferencePrice
{
    private readonly Dictionary<string, decimal> _map;

    public StaticFallback(params (string Symbol, decimal Price)[] entries)
    {
        _map = entries.ToDictionary(e => e.Symbol, e => e.Price, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string symbol, out decimal price) =>
        _map.TryGetValue(symbol, out price);
}

internal sealed class FakeMarketDataSubscriber : IMarketDataSubscriber
{
    public event Action<MarketTrade>? Trade;
    public event Action<MarketInfoSnapshot>? InfoSnapshot;
    public event Action<MarketDataConnectionState>? ConnectionStateChanged;
    public event Action<MarketSubscribeError>? SubscribeError;

    public MarketDataConnectionState State { get; private set; } = MarketDataConnectionState.Disconnected;
    public long DroppedEventCount => 0;
    public bool ConnectCalled { get; private set; }
    public List<string> Subscriptions { get; } = new();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ConnectCalled = true;
        State = MarketDataConnectionState.Connected;
        ConnectionStateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    public ValueTask SubscribeAsync(string symbol, CancellationToken ct = default)
    {
        Subscriptions.Add(symbol);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseTrade(string symbol, decimal price, DateTimeOffset ts) =>
        Trade?.Invoke(new MarketTrade(symbol, 0UL, price, ts));

    public void RaiseInfo(MarketInfoSnapshot s) => InfoSnapshot?.Invoke(s);

    public void RaiseSubscribeError(string symbol, string reason) =>
        SubscribeError?.Invoke(new MarketSubscribeError(symbol, reason));
}
