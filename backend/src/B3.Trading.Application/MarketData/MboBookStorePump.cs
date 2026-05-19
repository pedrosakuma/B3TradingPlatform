using Microsoft.Extensions.Hosting;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Q3.6 Stage A (#286). Wires the L3 / MBO events raised by
/// <see cref="IMarketDataSubscriber"/> into <see cref="MboBookStore"/>.
/// Same hosted-service lifecycle as <see cref="MarketDataPegBookPump"/>
/// so DI guarantees the handlers are attached before the subscriber
/// connects.
///
/// <para>
/// When <c>MarketDataOptions.EnableBook</c> is off the subscriber never
/// raises the Book* events; this pump is still wired (so the store can
/// be resolved as a singleton) but stays silent.
/// </para>
/// </summary>
public sealed class MboBookStorePump : IHostedService
{
    private readonly IMarketDataSubscriber _subscriber;
    private readonly MboBookStore _store;

    public MboBookStorePump(IMarketDataSubscriber subscriber, MboBookStore store)
    {
        _subscriber = subscriber;
        _store = store;
        _subscriber.BookSnapshot += OnSnapshot;
        _subscriber.OrderAdded += OnAdded;
        _subscriber.OrderUpdated += OnUpdated;
        _subscriber.OrderDeleted += OnDeleted;
        _subscriber.BookCleared += OnCleared;
    }

    private void OnSnapshot(MarketBookSnapshot s) => _store.ApplySnapshot(s);
    private void OnAdded(MarketOrderAdded e) => _store.ApplyAdded(e);
    private void OnUpdated(MarketOrderUpdated e) => _store.ApplyUpdated(e);
    private void OnDeleted(MarketOrderDeleted e) => _store.ApplyDeleted(e);
    private void OnCleared(MarketBookCleared e) => _store.ApplyCleared(e);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriber.BookSnapshot -= OnSnapshot;
        _subscriber.OrderAdded -= OnAdded;
        _subscriber.OrderUpdated -= OnUpdated;
        _subscriber.OrderDeleted -= OnDeleted;
        _subscriber.BookCleared -= OnCleared;
        return Task.CompletedTask;
    }
}
