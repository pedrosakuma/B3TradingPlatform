using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketPriceTrackerTests
{
    [Fact]
    public void TryGetReferencePrice_ReturnsFalse_BeforeAnyUpdate()
    {
        var tracker = new MarketPriceTracker();
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void OnTrade_UpdatesReferencePrice()
    {
        var tracker = new MarketPriceTracker();
        tracker.SetConnected(true);
        tracker.OnTrade("PETR4", 31.50m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(31.50m, price);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OnTrade_IgnoresNonPositivePrices(decimal badPrice)
    {
        var tracker = new MarketPriceTracker();
        tracker.OnTrade("PETR4", badPrice);
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void OnInfoSnapshot_PrefersTradingReferencePriceOverLastTradePrice()
    {
        var tracker = new MarketPriceTracker();
        tracker.SetConnected(true);
        tracker.OnInfoSnapshot("PETR4", tradingReferencePrice: 32m, lastTradePrice: 31m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(32m, price);
    }

    [Fact]
    public void OnInfoSnapshot_FallsBackToLastTradePrice_WhenTradingReferencePriceMissing()
    {
        var tracker = new MarketPriceTracker();
        tracker.SetConnected(true);
        tracker.OnInfoSnapshot("PETR4", tradingReferencePrice: null, lastTradePrice: 31m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(31m, price);
    }

    [Fact]
    public void OnInfoSnapshot_NoOp_WhenBothPricesMissing()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnInfoSnapshot("PETR4", tradingReferencePrice: null, lastTradePrice: null);
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void OnInfoSnapshot_DoesNotUseLastTradeWhenPublishedReferenceIsInvalid()
    {
        var tracker = new MarketPriceTracker();
        tracker.SetConnected(true);

        tracker.OnInfoSnapshot("PETR4", tradingReferencePrice: 0m, lastTradePrice: 31m);

        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void IsDelisted_IsFalseByDefault_AndTrueAfterNotification()
    {
        var tracker = new MarketPriceTracker();
        Assert.False(tracker.IsDelisted("PETR4"));
        tracker.OnSymbolDelisted("PETR4");
        Assert.True(tracker.IsDelisted("PETR4"));
    }

    [Fact]
    public void PricesAndDelistingAreTrackedIndependentlyPerSymbol()
    {
        var tracker = new MarketPriceTracker();
        tracker.SetConnected(true);
        tracker.OnTrade("PETR4", 30m);
        tracker.OnTrade("VALE3", 60m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var petr));
        Assert.True(tracker.TryGetReferencePrice("VALE3", out var vale));
        Assert.Equal(30m, petr);
        Assert.Equal(60m, vale);
    }

    [Fact]
    public void TryGetReferencePrice_ReturnsFalse_WhenNotConnected()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnTrade("PETR4", 30m);
        // Never called SetConnected(true) — default state is "not
        // connected", so cached prices must not be served.
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void TryGetReferencePrice_ServesCachedPrice_OnceConnected()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnTrade("PETR4", 30m);
        tracker.SetConnected(true);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(30m, price);
    }

    [Fact]
    public void TryGetReferencePrice_StopsServing_AfterDisconnect_ButKeepsCacheForReconnect()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnTrade("PETR4", 30m);
        tracker.SetConnected(true);
        tracker.SetConnected(false);
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));

        // Reconnecting resumes serving the same cached value immediately
        // — it wasn't cleared, only gated.
        tracker.SetConnected(true);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(30m, price);
    }

    [Fact]
    public void StrictAvailability_RequiresFreshCurrentEpochReference()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var tracker = new MarketPriceTracker(clock);
        tracker.OnTrade("PETR4", 30m);

        tracker.SetConnected(true);

        var unavailable = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.False(unavailable.IsEligible);
        Assert.Equal(FeedUnavailableReason.AwaitingCurrentEpochReference, unavailable.UnavailableReason);
        Assert.Equal(1, unavailable.ConnectionEpoch);

        tracker.OnTrade("PETR4", 31m);
        var available = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.True(available.IsEligible);
        Assert.Equal(1, available.LastValidMark?.ConnectionEpoch);
        Assert.Equal(ReferencePriceSource.Trade, available.LastValidMark?.Source);
        Assert.Equal(clock.GetUtcNow(), available.LastValidMark?.ReceivedAtUtc);
        Assert.Equal(clock.GetUtcNow(), available.ConnectionStartedAtUtc);
    }

    [Fact]
    public void StrictAvailability_RejectsPreviousEpochCacheAfterReconnect()
    {
        var tracker = new MarketPriceTracker();
        tracker.SetConnected(true);
        tracker.OnTrade("PETR4", 30m);
        Assert.True(tracker.GetAvailability("PETR4", TimeSpan.FromMinutes(1)).IsEligible);

        tracker.SetConnected(false);
        Assert.Equal(
            FeedUnavailableReason.Disconnected,
            tracker.GetAvailability("PETR4", TimeSpan.FromMinutes(1)).UnavailableReason);
        tracker.SetConnected(true);

        var reconnected = tracker.GetAvailability("PETR4", TimeSpan.FromMinutes(1));
        Assert.False(reconnected.IsEligible);
        Assert.Equal(FeedUnavailableReason.AwaitingCurrentEpochReference, reconnected.UnavailableReason);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var staticPolicyPrice));
        Assert.Equal(30m, staticPolicyPrice);
    }

    [Fact]
    public void StrictAvailability_BecomesStaleAtConfiguredAge()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var tracker = new MarketPriceTracker(clock);
        tracker.SetConnected(true);
        tracker.OnInfoSnapshot("PETR4", 30m, 29m);

        clock.Advance(TimeSpan.FromSeconds(11));

        var availability = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.False(availability.IsEligible);
        Assert.Equal(FeedUnavailableReason.StaleReference, availability.UnavailableReason);
        Assert.Equal(TimeSpan.FromSeconds(11), availability.ReferenceAge);
        Assert.Equal(ReferencePriceSource.TradingReferencePrice, availability.LastValidMark?.Source);
    }

    [Fact]
    public void StrictAvailability_DelayedPreviousEpochEventAfterReconnectDoesNotRestore()
    {
        var t0 = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
        var clock = new ManualTimeProvider(t0);
        var tracker = new MarketPriceTracker(clock);
        tracker.SetConnected(true, t0);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.OnTrade("PETR4", 30m, t0.AddSeconds(1));
        tracker.SetConnected(false, t0.AddSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.SetConnected(true, t0.AddSeconds(3));

        tracker.OnTrade("PETR4", 29m, t0.AddSeconds(1));

        var delayed = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.False(delayed.IsEligible);
        Assert.Equal(FeedUnavailableReason.AwaitingCurrentEpochReference, delayed.UnavailableReason);
        Assert.Equal(t0.AddSeconds(3), delayed.ConnectionStartedAtUtc);
        Assert.Equal(t0.AddSeconds(1), delayed.LastValidMark?.ReceivedAtUtc);
        Assert.Equal(0, delayed.LastValidMark?.ConnectionEpoch);

        tracker.OnTrade("PETR4", 31m, t0.AddSeconds(3));
        var restored = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.True(restored.IsEligible);
        Assert.Equal(31m, restored.LastValidMark?.Price);
        Assert.Equal(2, restored.LastValidMark?.ConnectionEpoch);
    }

    [Fact]
    public void StrictAvailability_DelayedDispatchArrivingAlreadyStaleDoesNotRestore()
    {
        var t0 = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
        var clock = new ManualTimeProvider(t0);
        var tracker = new MarketPriceTracker(clock);
        tracker.SetConnected(true, t0);
        clock.Advance(TimeSpan.FromSeconds(20));

        tracker.OnInfoSnapshot("PETR4", 30m, null, t0.AddSeconds(1));

        var availability = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.False(availability.IsEligible);
        Assert.Equal(FeedUnavailableReason.StaleReference, availability.UnavailableReason);
        Assert.Equal(TimeSpan.FromSeconds(19), availability.ReferenceAge);
        Assert.Equal(t0.AddSeconds(1), availability.LastValidMark?.ReceivedAtUtc);
    }

    [Fact]
    public void StrictAvailability_EpochAndMaxAgeBoundariesAreInclusive()
    {
        var t0 = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
        var clock = new ManualTimeProvider(t0);
        var tracker = new MarketPriceTracker(clock);
        tracker.SetConnected(true, t0);
        tracker.OnTrade("PETR4", 30m, t0);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10)).IsEligible);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(
            FeedUnavailableReason.StaleReference,
            tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10)).UnavailableReason);
    }

    [Fact]
    public void StrictAvailability_FutureTimestampNeverBecomesEligibleWithoutNewEvent()
    {
        var t0 = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
        var clock = new ManualTimeProvider(t0);
        var tracker = new MarketPriceTracker(clock);
        tracker.SetConnected(true, t0);

        Assert.False(tracker.OnTrade("PETR4", 30m, t0.AddSeconds(1)));
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
        Assert.Equal(
            FeedUnavailableReason.AwaitingCurrentEpochReference,
            tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10)).UnavailableReason);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
        Assert.Equal(
            FeedUnavailableReason.AwaitingCurrentEpochReference,
            tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10)).UnavailableReason);

        tracker.OnTrade("PETR4", 31m, clock.GetUtcNow());
        Assert.True(tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10)).IsEligible);
    }

    [Fact]
    public void OutOfOrderOlderTimestampDoesNotReplacePriceSourceOrTime()
    {
        var t0 = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
        var clock = new ManualTimeProvider(t0.AddSeconds(2));
        var tracker = new MarketPriceTracker(clock);
        tracker.SetConnected(true, t0);
        Assert.True(tracker.OnInfoSnapshot("PETR4", 31m, null, t0.AddSeconds(2)));

        Assert.False(tracker.OnTrade("PETR4", 29m, t0.AddSeconds(1)));

        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(31m, price);
        var availability = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.True(availability.IsEligible);
        Assert.Equal(31m, availability.LastValidMark?.Price);
        Assert.Equal(ReferencePriceSource.TradingReferencePrice, availability.LastValidMark?.Source);
        Assert.Equal(t0.AddSeconds(2), availability.LastValidMark?.ReceivedAtUtc);
    }

    [Fact]
    public void EqualTimestampLatestCallbackDeterminesPriceAndSource()
    {
        var t0 = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
        var clock = new ManualTimeProvider(t0);
        var tracker = new MarketPriceTracker(clock);
        tracker.SetConnected(true, t0);
        tracker.OnInfoSnapshot("PETR4", 31m, null, t0);

        Assert.True(tracker.OnTrade("PETR4", 32m, t0));

        var availability = tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10));
        Assert.Equal(32m, availability.LastValidMark?.Price);
        Assert.Equal(ReferencePriceSource.Trade, availability.LastValidMark?.Source);
        Assert.Equal(t0, availability.LastValidMark?.ReceivedAtUtc);
    }

    [Fact]
    public void SubscriptionError_IsPerSymbol_AndFreshUpdateRecoversIt()
    {
        var tracker = new MarketPriceTracker();
        tracker.SetConnected(true);
        tracker.OnTrade("PETR4", 30m);
        tracker.OnInfoSnapshot("VALE3", null, 70m);
        tracker.OnSubscriptionError("PETR4");

        Assert.Equal(
            FeedUnavailableReason.SubscriptionError,
            tracker.GetAvailability("PETR4", TimeSpan.FromMinutes(1)).UnavailableReason);
        var vale = tracker.GetAvailability("VALE3", TimeSpan.FromMinutes(1));
        Assert.True(vale.IsEligible);
        Assert.Equal(ReferencePriceSource.LastTradePrice, vale.LastValidMark?.Source);

        tracker.OnTrade("PETR4", 31m);
        Assert.True(tracker.GetAvailability("PETR4", TimeSpan.FromMinutes(1)).IsEligible);
    }

    [Fact]
    public void TryGetFreshMark_RequiresConnectionAndRecentUpdate()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var tracker = new MarketPriceTracker(clock);
        tracker.OnTrade("PETR4", 30m);

        Assert.False(tracker.TryGetFreshMark("PETR4", TimeSpan.FromSeconds(10), out _));

        tracker.SetConnected(true);
        Assert.True(tracker.TryGetFreshMark("PETR4", TimeSpan.FromSeconds(10), out var mark));
        Assert.Equal(30m, mark.Price);

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False(tracker.TryGetFreshMark("PETR4", TimeSpan.FromSeconds(10), out _));
        Assert.True(tracker.TryGetReferencePrice("PETR4", out _));
    }
}
