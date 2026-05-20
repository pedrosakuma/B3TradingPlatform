using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Bridges the in-host MBO-derived L2 view (<see cref="IL2BookView"/>)
/// into the Pegged-algo BBO cache (<see cref="PegBookTopCache"/>) so
/// <c>PegRef.Mid</c> and <c>PegRef.Best</c> resolve to real best-bid /
/// best-ask instead of transparently falling back to last-trade.
///
/// <para>
/// The live wire-path implementation of <see cref="IL2BookView"/> is
/// <c>SdkBookFeedAdapter</c> (host) backed by the SDK 0.4.0
/// <c>IBookFeed</c>; when MD is off or <c>EnableBook</c> is false a
/// no-op <see cref="InMemoryL2BookView"/> is wired instead and this
/// pump silently never fires — the engine falls back to last-trade
/// exactly as before, no behavioral regression.
/// </para>
///
/// <para>
/// <b>Thread-safety.</b> <see cref="IL2BookView.BookChanged"/> may fire
/// outside any internal lock, so the <see cref="IL2BookView.GetTopOfBook"/>
/// read here races newer mutations. That is intentional and correct:
/// each callback publishes a self-consistent snapshot to the cache; the
/// most recent writer wins, ordering preserved because the SDK delivers
/// per-symbol events on a single dispatch thread.
/// </para>
/// </summary>
public sealed class MboPegBookPump : IHostedService
{
    private readonly IL2BookView _store;
    private readonly PegBookTopCache _cache;
    private readonly ILogger<MboPegBookPump>? _logger;

    public MboPegBookPump(
        IL2BookView store,
        PegBookTopCache cache,
        ILogger<MboPegBookPump>? logger = null)
    {
        _store = store;
        _cache = cache;
        _logger = logger;
        _store.BookChanged += OnBookChanged;
    }

    private void OnBookChanged(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        try
        {
            var top = _store.GetTopOfBook(symbol);
            if (top is not { } t) return;
            // L2Side.OrderCount > 0 distinguishes a "real" side from
            // the sentinel (0,0,0) tuple GetTopOfBook returns when one
            // side is empty. We pass null for an empty side so the
            // cache's existing-value-preserving merge does not clobber
            // a previously-known BBO leg with zero.
            decimal? bid = t.Bid.OrderCount > 0 ? t.Bid.Price : (decimal?)null;
            decimal? ask = t.Ask.OrderCount > 0 ? t.Ask.Price : (decimal?)null;
            if (bid is null && ask is null) return;
            _cache.UpdateBookTop(symbol, bid, ask, t.UpdatedUtc);
        }
        catch (Exception ex)
        {
            // BookChanged is invoked from the SDK dispatch thread; an
            // unhandled exception here would propagate into the store
            // and starve all other listeners (auction sink, WS book
            // sink). Swallow + log so a Pegged-cache hiccup never
            // breaks the wider book pipeline.
            _logger?.LogWarning(ex,
                "MboPegBookPump failed to propagate BBO for {Symbol}; cache leg left at previous value.",
                symbol);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.BookChanged -= OnBookChanged;
        return Task.CompletedTask;
    }
}
