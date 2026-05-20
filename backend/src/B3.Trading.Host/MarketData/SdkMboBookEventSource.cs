using B3.MarketData.WebSocketClient;
using B3.Trading.Application.MarketData;
using AppMarketBookSnapshot = B3.Trading.Application.MarketData.MarketBookSnapshot;
using AppMarketBookOrder = B3.Trading.Application.MarketData.MarketBookOrder;
using AppMarketOrderAdded = B3.Trading.Application.MarketData.MarketOrderAdded;
using AppMarketOrderUpdated = B3.Trading.Application.MarketData.MarketOrderUpdated;
using AppMarketOrderDeleted = B3.Trading.Application.MarketData.MarketOrderDeleted;
using AppMarketBookCleared = B3.Trading.Application.MarketData.MarketBookCleared;
using AppMarketBookSide = B3.Trading.Application.MarketData.MarketBookSide;
using AppMarketBookClearSide = B3.Trading.Application.MarketData.MarketBookClearSide;
using SdkBookSide = B3.MarketData.WebSocketClient.BookSide;
using SdkBookClearSide = B3.MarketData.WebSocketClient.BookClearSide;

namespace B3.Trading.Host.MarketData;

/// <summary>
/// Adapter from <see cref="MarketDataClient"/> raw MBO frames
/// (<c>BookSnapshot</c>, <c>OrderAdded</c>, <c>OrderUpdated</c>,
/// <c>OrderDeleted</c>, <c>BookCleared</c>) to the application-side
/// <see cref="IMboBookEventSource"/> seam consumed by
/// <c>WebSocketMboBookEventSink</c> (#372 / #293).
///
/// <para>
/// Subscribes in parallel to SDK 0.4.0's <see cref="IBookFeed"/> — the
/// BookFeed maintains a derived L2 view (used by
/// <see cref="SdkBookFeedAdapter"/>); this adapter forwards the raw
/// L3 events untouched so the WS L3 channel sees every per-order
/// delta. Both subscribers attach to the SAME <see cref="MarketDataClient"/>
/// instance — the SDK fans events out to every handler, no duplicate
/// network traffic.
/// </para>
///
/// <para>
/// Registered only when <c>MarketDataOptions.EnableBook</c> AND
/// <c>WsUrl</c> are both set (same gate as the L2 BookFeed). When the
/// live feed is off, <see cref="NullMboBookEventSource"/> is wired
/// instead — the sink resolves through DI either way.
/// </para>
/// </summary>
internal sealed class SdkMboBookEventSource : IMboBookEventSource, IDisposable
{
    private readonly MarketDataClient _client;

    public event Action<AppMarketBookSnapshot>? BookSnapshot;
    public event Action<AppMarketOrderAdded>? OrderAdded;
    public event Action<AppMarketOrderUpdated>? OrderUpdated;
    public event Action<AppMarketOrderDeleted>? OrderDeleted;
    public event Action<AppMarketBookCleared>? BookCleared;

    public SdkMboBookEventSource(MarketDataClient client)
    {
        _client = client;
        _client.BookSnapshot += OnBookSnapshot;
        _client.OrderAdded += OnOrderAdded;
        _client.OrderUpdated += OnOrderUpdated;
        _client.OrderDeleted += OnOrderDeleted;
        _client.BookCleared += OnBookCleared;
    }

    public void Dispose()
    {
        _client.BookSnapshot -= OnBookSnapshot;
        _client.OrderAdded -= OnOrderAdded;
        _client.OrderUpdated -= OnOrderUpdated;
        _client.OrderDeleted -= OnOrderDeleted;
        _client.BookCleared -= OnBookCleared;
    }

    private void OnBookSnapshot(BookSnapshotEvent ev)
    {
        var bids = new AppMarketBookOrder[ev.Bids.Count];
        for (var i = 0; i < ev.Bids.Count; i++)
            bids[i] = new AppMarketBookOrder(ev.Bids[i].OrderId, ev.Bids[i].Price, ev.Bids[i].Qty);
        var asks = new AppMarketBookOrder[ev.Asks.Count];
        for (var i = 0; i < ev.Asks.Count; i++)
            asks[i] = new AppMarketBookOrder(ev.Asks[i].OrderId, ev.Asks[i].Price, ev.Asks[i].Qty);
        BookSnapshot?.Invoke(new AppMarketBookSnapshot
        {
            Symbol = ev.Symbol,
            SecurityId = ev.SecurityId,
            RptSeq = ev.RptSeq,
            Bids = bids,
            Asks = asks,
            ReceivedUtc = NormalizeUtc(ev.ReceivedUtc),
        });
    }

    private void OnOrderAdded(OrderAddedEvent ev) =>
        OrderAdded?.Invoke(new AppMarketOrderAdded(
            ev.Symbol, ev.SecurityId, ev.OrderId, ToAppSide(ev.Side),
            ev.Price, ev.Qty, NormalizeUtc(ev.ReceivedUtc)));

    private void OnOrderUpdated(OrderUpdatedEvent ev) =>
        OrderUpdated?.Invoke(new AppMarketOrderUpdated(
            ev.Symbol, ev.SecurityId, ev.OrderId, ToAppSide(ev.Side),
            ev.Price, ev.Qty, NormalizeUtc(ev.ReceivedUtc)));

    private void OnOrderDeleted(OrderDeletedEvent ev) =>
        OrderDeleted?.Invoke(new AppMarketOrderDeleted(
            ev.Symbol, ev.SecurityId, ev.OrderId, ToAppSide(ev.Side),
            NormalizeUtc(ev.ReceivedUtc)));

    private void OnBookCleared(BookClearedEvent ev) =>
        BookCleared?.Invoke(new AppMarketBookCleared(
            ev.Symbol, ev.SecurityId, ToAppClearSide(ev.ClearSide),
            NormalizeUtc(ev.ReceivedUtc)));

    private static AppMarketBookSide ToAppSide(SdkBookSide s) => s switch
    {
        SdkBookSide.Bid => AppMarketBookSide.Bid,
        SdkBookSide.Ask => AppMarketBookSide.Ask,
        _ => AppMarketBookSide.Bid,
    };

    private static AppMarketBookClearSide ToAppClearSide(SdkBookClearSide s) => s switch
    {
        SdkBookClearSide.Bid => AppMarketBookClearSide.Bid,
        SdkBookClearSide.Ask => AppMarketBookClearSide.Ask,
        SdkBookClearSide.Both => AppMarketBookClearSide.Both,
        _ => AppMarketBookClearSide.Both,
    };

    private static DateTimeOffset NormalizeUtc(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
