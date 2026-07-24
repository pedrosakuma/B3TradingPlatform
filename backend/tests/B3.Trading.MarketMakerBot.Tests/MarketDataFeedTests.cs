using B3.MarketData.WebSocketClient;
using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketDataFeedTests
{
    [Fact]
    public void NotifySymbolDelisted_UpdatesAvailabilityBeforeRaisingSignal()
    {
        var tracker = new MarketPriceTracker();
        var estimator = new VolatilitySpreadEstimator(
            Options.Create(new MarketMakerBotOptions()), TimeProvider.System);
        var feed = new MarketDataFeed(tracker, estimator, NullLogger.Instance);
        var observedDelisted = false;
        string? observedSymbol = null;
        feed.SymbolAvailabilityChanged += symbol =>
        {
            observedSymbol = symbol;
            observedDelisted = tracker.IsDelisted(symbol);
        };

        feed.NotifySymbolDelisted("PETR4");

        Assert.Equal("PETR4", observedSymbol);
        Assert.True(observedDelisted);
    }

    [Fact]
    public void TradeUpdates_SignalOnlyWhenEffectiveAdditionalTicksChange()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments =
            [
                new InstrumentConfig
                {
                    Symbol = "PETR4",
                    TickSize = 0.01m,
                    VolatilitySpread = new VolatilitySpreadConfig
                    {
                        Enabled = true,
                        MinSamples = 1,
                        MaxSamples = 10,
                        Window = TimeSpan.FromMinutes(1),
                        Multiplier = 1m,
                        MaxAdditionalSpreadTicks = 20,
                    },
                },
            ],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var feed = new MarketDataFeed(tracker, estimator, NullLogger.Instance);
        var signals = new List<string>();
        feed.VolatilitySpreadChanged += signals.Add;
        feed.NotifyConnectionState(true);

        feed.NotifyTrade("PETR4", 30m);
        feed.NotifyInfoSnapshot("PETR4", 100m, 100m);
        feed.NotifyInfoSnapshot("PETR4", 101m, 101m);
        feed.NotifyTrade("PETR4", 30.02m);
        feed.NotifyTrade("PETR4", 30.04m);
        feed.NotifyTrade("PETR4", 30.04m);
        feed.NotifyTrade("PETR4", 30.04m);

        Assert.Equal(["PETR4", "PETR4"], signals);
        Assert.Equal(1, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);
    }

    [Fact]
    public void DisconnectAndReconnect_SignalStaticFallbackAndRetainedWidening()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments =
            [
                new InstrumentConfig
                {
                    Symbol = "PETR4",
                    TickSize = 0.01m,
                    VolatilitySpread = new VolatilitySpreadConfig
                    {
                        Enabled = true,
                        MinSamples = 1,
                        MaxSamples = 10,
                        Window = TimeSpan.FromMinutes(1),
                        Multiplier = 1m,
                        MaxAdditionalSpreadTicks = 20,
                    },
                },
            ],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var feed = new MarketDataFeed(tracker, estimator, NullLogger.Instance);
        var signals = 0;
        feed.VolatilitySpreadChanged += _ => signals++;
        feed.NotifyConnectionState(true);
        feed.NotifyTrade("PETR4", 30m);
        feed.NotifyTrade("PETR4", 30.02m);
        signals = 0;

        feed.NotifyConnectionState(false);
        Assert.Equal(0, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);
        feed.NotifyConnectionState(true);

        Assert.Equal(2, signals);
        Assert.Equal(2, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);
    }

    [Fact]
    public void ConnectionEligibility_SignalsEveryTransitionOnce_IndependentlyOfVolatilityAndDelisting()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments = [new InstrumentConfig { Symbol = "PETR4" }],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var feed = new MarketDataFeed(tracker, estimator, NullLogger.Instance);
        var connectionSignals = 0;
        var availabilitySignals = 0;
        feed.ConnectionEligibilityChanged += () => connectionSignals++;
        feed.SymbolAvailabilityChanged += _ => availabilitySignals++;

        feed.NotifyConnectionState(true);
        feed.NotifyConnectionState(true);
        feed.NotifyConnectionState(false);
        feed.NotifyConnectionState(false);
        feed.NotifyConnectionState(true);
        feed.NotifySymbolDelisted("PETR4");

        Assert.Equal(3, connectionSignals);
        Assert.Equal(1, availabilitySignals);
    }

    [Fact]
    public async Task PauseAndCancel_InitialFailureRetriesInBackgroundWithoutDuplicateLifecycle()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments = [new InstrumentConfig { Symbol = "PETR4" }],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var failed = new FakeMarketDataClient
        {
            ConnectHandler = _ => Task.FromException(new InvalidOperationException("initial failure")),
        };
        var failedAgain = new FakeMarketDataClient
        {
            ConnectHandler = _ => Task.FromException(new InvalidOperationException("second failure")),
        };
        var connected = new FakeMarketDataClient();
        var factory = new FakeMarketDataClientFactory(failed, failedAgain, connected);
        var delays = new List<TimeSpan>();
        await using var feed = new MarketDataFeed(
            tracker,
            estimator,
            NullLogger.Instance,
            TimeProvider.System,
            factory,
            (delay, _, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await feed.StartAsync(
            new MarketDataOptions
            {
                WsUrl = "ws://marketdata.test/ws",
                FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
                MaxReferenceAge = TimeSpan.FromSeconds(10),
            },
            options.Value.Instruments,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.Equal(3, factory.CreateCount);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
        Assert.True(failed.Disposed);
        Assert.Equal(failed.HandlerAdds, failed.HandlerRemoves);
        Assert.True(failedAgain.Disposed);
        Assert.Equal(failedAgain.HandlerAdds, failedAgain.HandlerRemoves);
        Assert.Equal(["PETR4"], connected.Subscriptions);
        Assert.Equal(8, connected.HandlerAdds);
        Assert.Equal(0, connected.HandlerRemoves);
        Assert.All(factory.CreatedOptions, created => Assert.True(created.AutoResubscribeOnReconnect));
    }

    [Fact]
    public async Task PauseAndCancel_DisposeCancelsPendingConnectAndUnhooksHandlers()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments = [new InstrumentConfig { Symbol = "PETR4" }],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var connecting = new FakeMarketDataClient
        {
            ConnectHandler = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct),
        };
        var factory = new FakeMarketDataClientFactory(connecting);
        var feed = new MarketDataFeed(
            tracker,
            estimator,
            NullLogger.Instance,
            TimeProvider.System,
            factory);

        await feed.StartAsync(
            new MarketDataOptions
            {
                WsUrl = "ws://marketdata.test/ws",
                FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
                MaxReferenceAge = TimeSpan.FromSeconds(10),
            },
            options.Value.Instruments,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await feed.DisposeAsync();

        Assert.True(connecting.Disposed);
        Assert.Equal(connecting.HandlerAdds, connecting.HandlerRemoves);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task PauseAndCancel_DisposeCollectsClientThatCompletesDuringShutdown()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments = [new InstrumentConfig { Symbol = "PETR4" }],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var connectCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connecting = new FakeMarketDataClient
        {
            ConnectHandler = _ => connectCompletion.Task,
        };
        var feed = new MarketDataFeed(
            tracker,
            estimator,
            NullLogger.Instance,
            TimeProvider.System,
            new FakeMarketDataClientFactory(connecting));
        await feed.StartAsync(
            new MarketDataOptions
            {
                WsUrl = "ws://marketdata.test/ws",
                FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
                MaxReferenceAge = TimeSpan.FromSeconds(10),
            },
            options.Value.Instruments,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var dispose = feed.DisposeAsync().AsTask();
        connectCompletion.TrySetResult();
        await dispose;

        Assert.True(connecting.Disposed);
        Assert.Equal(connecting.HandlerAdds, connecting.HandlerRemoves);
    }

    [Fact]
    public async Task StaticRefPrice_InitialFailureDoesNotStartBackgroundRetries()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments = [new InstrumentConfig { Symbol = "PETR4" }],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var failed = new FakeMarketDataClient
        {
            ConnectHandler = _ => Task.FromException(new InvalidOperationException("initial failure")),
        };
        var factory = new FakeMarketDataClientFactory(failed);
        var delayCalls = 0;
        await using var feed = new MarketDataFeed(
            tracker,
            estimator,
            NullLogger.Instance,
            TimeProvider.System,
            factory,
            (_, _, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        await feed.StartAsync(
            new MarketDataOptions
            {
                WsUrl = "ws://marketdata.test/ws",
                FeedLossPolicy = FeedLossPolicy.StaticRefPrice,
            },
            options.Value.Instruments,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, delayCalls);
        Assert.True(failed.Disposed);
        Assert.Equal(failed.HandlerAdds, failed.HandlerRemoves);
    }

    [Fact]
    public async Task PauseAndCancel_FirstFreshCurrentEpochUpdateRaisesAvailabilitySignal()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments = [new InstrumentConfig { Symbol = "PETR4" }],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var client = new FakeMarketDataClient();
        await using var feed = new MarketDataFeed(
            tracker,
            estimator,
            NullLogger.Instance,
            TimeProvider.System,
            new FakeMarketDataClientFactory(client));
        var signals = new List<string>();
        feed.SymbolAvailabilityChanged += signals.Add;
        await feed.StartAsync(
            new MarketDataOptions
            {
                WsUrl = "ws://marketdata.test/ws",
                FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
                MaxReferenceAge = TimeSpan.FromSeconds(10),
            },
            options.Value.Instruments,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        feed.NotifyTrade("PETR4", 30m);
        feed.NotifyTrade("PETR4", 31m);

        Assert.Equal(["PETR4"], signals);
        Assert.True(tracker.GetAvailability("PETR4", TimeSpan.FromSeconds(10)).IsEligible);
    }

    private sealed class FakeMarketDataClientFactory(params FakeMarketDataClient[] clients)
        : IMarketDataClientFactory
    {
        private readonly Queue<FakeMarketDataClient> _clients = new(clients);
        public int CreateCount { get; private set; }
        public List<MarketDataClientOptions> CreatedOptions { get; } = [];

        public IMarketDataClient Create(MarketDataClientOptions options, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
        {
            CreateCount++;
            CreatedOptions.Add(options);
            return _clients.Dequeue();
        }
    }

    private sealed class FakeMarketDataClient : IMarketDataClient
    {
        private Action<TradeEvent>? _trade;
        private Action<InfoSnapshotEvent>? _infoSnapshot;
        private Action<SymbolDelistedEvent>? _symbolDelisted;
        private Action<ConnectionStateChangedEvent>? _connectionStateChanged;
        private Action<SubscribeErrorEvent>? _subscribeError;
        private Action<OrderAddedEvent>? _orderAdded;
        private Action<OrderUpdatedEvent>? _orderUpdated;
        private Action<OrderDeletedEvent>? _orderDeleted;

        public Func<CancellationToken, Task> ConnectHandler { get; set; } = _ => Task.CompletedTask;
        public List<string> Subscriptions { get; } = [];
        public int HandlerAdds { get; private set; }
        public int HandlerRemoves { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<TradeEvent>? Trade
        {
            add { _trade += value; HandlerAdds++; }
            remove { _trade -= value; HandlerRemoves++; }
        }
        public event Action<InfoSnapshotEvent>? InfoSnapshot
        {
            add { _infoSnapshot += value; HandlerAdds++; }
            remove { _infoSnapshot -= value; HandlerRemoves++; }
        }
        public event Action<SymbolDelistedEvent>? SymbolDelisted
        {
            add { _symbolDelisted += value; HandlerAdds++; }
            remove { _symbolDelisted -= value; HandlerRemoves++; }
        }
        public event Action<ConnectionStateChangedEvent>? ConnectionStateChanged
        {
            add { _connectionStateChanged += value; HandlerAdds++; }
            remove { _connectionStateChanged -= value; HandlerRemoves++; }
        }
        public event Action<SubscribeErrorEvent>? SubscribeError
        {
            add { _subscribeError += value; HandlerAdds++; }
            remove { _subscribeError -= value; HandlerRemoves++; }
        }
        public event Action<OrderAddedEvent>? OrderAdded
        {
            add { _orderAdded += value; HandlerAdds++; }
            remove { _orderAdded -= value; HandlerRemoves++; }
        }
        public event Action<OrderUpdatedEvent>? OrderUpdated
        {
            add { _orderUpdated += value; HandlerAdds++; }
            remove { _orderUpdated -= value; HandlerRemoves++; }
        }
        public event Action<OrderDeletedEvent>? OrderDeleted
        {
            add { _orderDeleted += value; HandlerAdds++; }
            remove { _orderDeleted -= value; HandlerRemoves++; }
        }

        public Task ConnectAsync(CancellationToken ct) => ConnectHandler(ct);

        public ValueTask SubscribeAsync(string symbol, SubscribeFlags flags, CancellationToken ct)
        {
            Subscriptions.Add(symbol);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
