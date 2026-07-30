using B3.MarketData.WebSocketClient;
using B3.Trading.SampleBot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.SampleBot.Tests;

public class MarketDataPlatformClientTests
{
    [Fact]
    public async Task RunAsync_BookOrderSuppliesPublicReferencePrice()
    {
        using var lifetime = new CancellationTokenSource();
        var sdk = new FakeMarketDataClient();
        var observer = new RecordingMarketDataObserver(lifetime);
        var client = new MarketDataPlatformClient(
            Options.Create(new SampleBotOptions
            {
                MarketData = new SampleBotMarketDataOptions
                {
                    WsUrl = "ws://marketdata:8080/ws",
                },
            }),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<MarketDataPlatformClient>.Instance,
            new FakeMarketDataClientFactory(sdk));

        await client.RunAsync(observer, "PETR4", lifetime.Token);

        Assert.Equal("PETR4", sdk.SubscribedSymbol);
        Assert.Equal(
            SubscribeFlags.Info | SubscribeFlags.Trades | SubscribeFlags.Book,
            sdk.SubscribedFlags);
        var quote = Assert.Single(observer.Quotes);
        Assert.Equal("PETR4", quote.Symbol);
        Assert.Equal(1234UL, quote.SecurityId);
        Assert.Equal(ReferencePriceSource.BookOrder, quote.Source);
        Assert.Equal(30.05m, quote.Price);
    }

    private sealed class RecordingMarketDataObserver : ISampleBotMarketDataObserver
    {
        private readonly CancellationTokenSource _lifetime;

        public RecordingMarketDataObserver(CancellationTokenSource lifetime)
        {
            _lifetime = lifetime;
        }

        public List<MarketDataQuote> Quotes { get; } = new();

        public Task OnConnectedAsync(bool isReconnect, CancellationToken cancellationToken) =>
            ClearQuotes();

        private Task ClearQuotes()
        {
            Quotes.Clear();
            return Task.CompletedTask;
        }

        public Task OnDisconnectedAsync(Exception? error, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task OnQuoteAsync(MarketDataQuote quote, CancellationToken cancellationToken)
        {
            Quotes.Add(quote);
            _lifetime.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMarketDataClientFactory : IMarketDataClientFactory
    {
        private readonly IMarketDataClient _client;

        public FakeMarketDataClientFactory(IMarketDataClient client)
        {
            _client = client;
        }

        public IMarketDataClient Create(
            MarketDataClientOptions options,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) =>
            _client;
    }

    private sealed class FakeMarketDataClient : IMarketDataClient
    {
        private Action<OrderAddedEvent>? _orderAdded;
        private Action<BookSnapshotEvent>? _bookSnapshot;

        public event Action<TradeEvent>? Trade
        {
            add { }
            remove { }
        }

        public event Action<InfoSnapshotEvent>? InfoSnapshot
        {
            add { }
            remove { }
        }

        public event Action<BookSnapshotEvent>? BookSnapshot
        {
            add => _bookSnapshot += value;
            remove => _bookSnapshot -= value;
        }

        public event Action<OrderAddedEvent>? OrderAdded
        {
            add => _orderAdded += value;
            remove => _orderAdded -= value;
        }

        public event Action<OrderUpdatedEvent>? OrderUpdated
        {
            add { }
            remove { }
        }

        public event Action<SymbolDelistedEvent>? SymbolDelisted
        {
            add { }
            remove { }
        }

        public event Action<ConnectionStateChangedEvent>? ConnectionStateChanged
        {
            add { }
            remove { }
        }

        public event Action<SubscribeErrorEvent>? SubscribeError
        {
            add { }
            remove { }
        }

        public string? SubscribedSymbol { get; private set; }

        public SubscribeFlags SubscribedFlags { get; private set; }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask SubscribeAsync(string symbol, SubscribeFlags flags, CancellationToken ct)
        {
            SubscribedSymbol = symbol;
            SubscribedFlags = flags;
            _bookSnapshot?.Invoke(new BookSnapshotEvent
            {
                SecurityId = 1234,
                Symbol = symbol,
                Asks = [new BookSnapshotOrder(99, 30.05m, 100)],
                ReceivedUtc = DateTime.UtcNow,
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
