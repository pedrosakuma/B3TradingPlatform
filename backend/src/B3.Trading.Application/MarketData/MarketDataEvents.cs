namespace B3.Trading.Application.MarketData;

/// <summary>
/// Application-owned market-data event records. Decouple risk logic
/// from the upstream <c>B3.MarketData.WebSocketClient</c> SDK shapes:
/// <c>SdkMarketDataSubscriber</c> in the host translates the SDK
/// events into these on the way in, so anything below this layer
/// (tests, alternate transports) only needs to deal with our own
/// types.
/// </summary>
public readonly record struct MarketTrade(
    string Symbol,
    ulong SecurityId,
    decimal Price,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// Snapshot view of an instrument's reference data (open / close /
/// last trade / trading reference). Only the fields we currently use
/// are surfaced; if a future risk check needs e.g. <c>VwapPrice</c>,
/// add it here and propagate.
/// </summary>
public readonly record struct MarketInfoSnapshot(
    string Symbol,
    ulong SecurityId,
    decimal? LastTradePrice,
    decimal? TradingReferencePrice,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// Coarse connection state of the underlying market-data feed.
/// Mirrors <c>B3.MarketData.WebSocketClient.ConnectionState</c> 1:1
/// but kept app-owned so tests don't reference the SDK enum.
/// </summary>
public enum MarketDataConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted,
}
