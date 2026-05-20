namespace B3.Trading.Application.MarketData;

/// <summary>
/// Application-side seam over the raw L3 / MBO frames the upstream
/// market-data SDK exposes. Separate from <see cref="IL2BookView"/>
/// (which is a derived, aggregated view): consumers that need
/// per-order fidelity (e.g. the public <c>bookmbo.${symbol}</c> WS
/// channel, #372 / #293) subscribe here instead of asking the L2
/// view for ladder deltas.
///
/// <para>
/// Implementations MUST raise events in the order they arrive from
/// the wire so consumers can rebuild a deterministic per-symbol L3
/// state by applying snapshot → add/update/delete deltas. Events
/// MAY race across symbols on different threads, but per-symbol
/// ordering MUST be preserved.
/// </para>
///
/// <para>
/// The host-side <c>SdkMboBookEventSource</c> wires this seam to
/// <c>B3.MarketData.WebSocketClient.MarketDataClient</c>'s
/// <c>BookSnapshot</c> / <c>OrderAdded</c> / <c>OrderUpdated</c> /
/// <c>OrderDeleted</c> / <c>BookCleared</c> events. Tests use a fake
/// implementation that raises the events synchronously.
/// </para>
///
/// <para>
/// When the live market-data feed is off (no <c>WsUrl</c> or
/// <c>EnableBook=false</c>) this seam is registered as a no-op (no
/// events ever raised); WS subscribers to <c>bookmbo.${symbol}</c>
/// still receive the empty cold snapshot and then sit idle.
/// </para>
/// </summary>
public interface IMboBookEventSource
{
    event Action<MarketBookSnapshot>? BookSnapshot;
    event Action<MarketOrderAdded>? OrderAdded;
    event Action<MarketOrderUpdated>? OrderUpdated;
    event Action<MarketOrderDeleted>? OrderDeleted;
    event Action<MarketBookCleared>? BookCleared;
}

/// <summary>
/// Default no-op implementation. Used when the live wire path is
/// disabled so the WS sink can resolve through DI without conditional
/// registration on consumer side.
/// </summary>
public sealed class NullMboBookEventSource : IMboBookEventSource
{
    public event Action<MarketBookSnapshot>? BookSnapshot;
    public event Action<MarketOrderAdded>? OrderAdded;
    public event Action<MarketOrderUpdated>? OrderUpdated;
    public event Action<MarketOrderDeleted>? OrderDeleted;
    public event Action<MarketBookCleared>? BookCleared;

    // Suppress "field is never used" — the events exist only to satisfy
    // the contract for consumers that may attach handlers.
    private void _suppress()
    {
        _ = BookSnapshot;
        _ = OrderAdded;
        _ = OrderUpdated;
        _ = OrderDeleted;
        _ = BookCleared;
    }
}
