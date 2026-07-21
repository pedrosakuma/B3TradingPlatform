using System.Collections.Concurrent;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Thread-safe last-known-price cache fed by <see cref="MarketDataFeed"/>.
/// Once market data starts flowing, it anchors the market maker's
/// quotes instead of each instrument's static config
/// <see cref="InstrumentConfig.RefPrice"/>; before the first update (or
/// whenever the feed is disabled/disconnected) callers fall back to
/// that config value. Also tracks which symbols the venue has reported
/// as delisted, so the worker can pause quoting them instead of
/// resting orders against an instrument that no longer trades.
/// </summary>
public sealed class MarketPriceTracker
{
    private readonly ConcurrentDictionary<string, decimal> _referencePrice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _delisted = new(StringComparer.Ordinal);

    public bool TryGetReferencePrice(string symbol, out decimal price) =>
        _referencePrice.TryGetValue(symbol, out price);

    public bool IsDelisted(string symbol) => _delisted.ContainsKey(symbol);

    public void OnTrade(string symbol, decimal price)
    {
        if (price > 0m) _referencePrice[symbol] = price;
    }

    /// <summary>Prefers the venue's own <c>TradingReferencePrice</c> — the
    /// authoritative anchor B3 itself publishes — falling back to
    /// <c>LastTradePrice</c> when the venue hasn't set one yet (e.g.
    /// before the first trade of the day).</summary>
    public void OnInfoSnapshot(string symbol, decimal? tradingReferencePrice, decimal? lastTradePrice)
    {
        var candidate = tradingReferencePrice ?? lastTradePrice;
        if (candidate is { } p && p > 0m) _referencePrice[symbol] = p;
    }

    public void OnSymbolDelisted(string symbol) => _delisted[symbol] = 0;
}
