namespace B3.Trading.Application.MarketData;

/// <summary>
/// No-op <see cref="IMarketDataSubscriber"/> used when the live WS
/// feed is disabled (<c>Trading:MarketData:WsUrl</c> unset). Lets
/// downstream consumers (Q1.5 <c>AuctionStateStore</c>,
/// <c>IPhaseProvider</c>) be wired unconditionally — they simply
/// never receive events. <see cref="ConnectAsync"/> /
/// <see cref="SubscribeAsync"/> are no-ops.
/// </summary>
public sealed class NullMarketDataSubscriber : IMarketDataSubscriber
{
#pragma warning disable CS0067 // events deliberately never raised
    public event Action<MarketTrade>? Trade;
    public event Action<MarketInfoSnapshot>? InfoSnapshot;
    public event Action<MarketDataConnectionState>? ConnectionStateChanged;
    public event Action<MarketSubscribeError>? SubscribeError;
    public event Action<MarketTheoreticalOpening>? TheoreticalOpening;
    public event Action<MarketAuctionImbalance>? AuctionImbalance;
    public event Action<MarketAuctionPrint>? AuctionPrint;
#pragma warning restore CS0067

    public MarketDataConnectionState State => MarketDataConnectionState.Disconnected;
    public long DroppedEventCount => 0;

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask SubscribeAsync(string symbol, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
