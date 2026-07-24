namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Thread-safe per-symbol reference-price and feed-readiness tracker. Static
/// fallback callers retain the historical connected-cache behavior through
/// <see cref="TryGetReferencePrice"/>; strict callers use
/// <see cref="GetAvailability"/> so a reconnect cannot reuse a previous
/// connection epoch's cached value.
/// </summary>
public sealed class MarketPriceTracker
{
    private readonly Dictionary<string, SymbolReferenceState> _symbols = new(StringComparer.Ordinal);
    private readonly HashSet<string> _delisted = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private bool _connected;
    private long _connectionEpoch;

    public MarketPriceTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Historical/default behavior: serve any cached reference while connected,
    /// including a value retained across reconnect.
    /// </summary>
    public bool TryGetReferencePrice(string symbol, out decimal price)
    {
        lock (_gate)
        {
            if (_connected &&
                _symbols.TryGetValue(symbol, out var state) &&
                state.LastValidMark is { } mark)
            {
                price = mark.Price;
                return true;
            }
        }

        price = default;
        return false;
    }

    public bool TryGetFreshMark(string symbol, TimeSpan maxAge, out MarketMark mark)
    {
        lock (_gate)
        {
            if (!_connected ||
                !_symbols.TryGetValue(symbol, out var state) ||
                state.LastValidMark is not { } found)
            {
                mark = default;
                return false;
            }

            mark = found;
            var age = _clock.GetUtcNow() - mark.ObservedAtUtc;
            return age >= TimeSpan.Zero && age <= maxAge;
        }
    }

    /// <summary>
    /// Strict per-symbol readiness. Eligibility requires a connected socket, no
    /// subscription error in the current epoch, a valid update stamped with the
    /// current epoch, and an age inside <paramref name="maxAge"/>.
    /// </summary>
    public ReferenceAvailability GetAvailability(string symbol, TimeSpan maxAge)
    {
        lock (_gate)
        {
            _symbols.TryGetValue(symbol, out var state);
            var mark = state?.LastValidMark;
            TimeSpan? age = mark is { } value ? _clock.GetUtcNow() - value.ObservedAtUtc : null;
            var reason = !_connected
                ? FeedUnavailableReason.Disconnected
                : state?.SubscriptionErrorEpoch == _connectionEpoch
                    ? FeedUnavailableReason.SubscriptionError
                    : mark is null || mark.Value.ConnectionEpoch != _connectionEpoch
                        ? FeedUnavailableReason.AwaitingCurrentEpochReference
                        : age < TimeSpan.Zero || age > maxAge
                            ? FeedUnavailableReason.StaleReference
                            : FeedUnavailableReason.None;
            return new ReferenceAvailability(
                reason == FeedUnavailableReason.None,
                reason,
                _connected,
                _connectionEpoch,
                mark,
                age);
        }
    }

    public bool TryGetEligibleReference(
        string symbol,
        TimeSpan maxAge,
        out MarketMark mark,
        out FeedUnavailableReason unavailableReason)
    {
        var availability = GetAvailability(symbol, maxAge);
        unavailableReason = availability.UnavailableReason;
        if (availability.IsEligible && availability.LastValidMark is { } found)
        {
            mark = found;
            return true;
        }

        mark = default;
        return false;
    }

    public bool IsDelisted(string symbol)
    {
        lock (_gate)
            return _delisted.Contains(symbol);
    }

    public bool OnTrade(string symbol, decimal price) =>
        Update(symbol, price, ReferencePriceSource.Trade);

    /// <summary>
    /// Prefers the venue's TradingReferencePrice, falling back to
    /// LastTradePrice only when the former is absent.
    /// </summary>
    public bool OnInfoSnapshot(string symbol, decimal? tradingReferencePrice, decimal? lastTradePrice)
    {
        if (tradingReferencePrice is { } reference)
            return reference > 0m &&
                Update(symbol, reference, ReferencePriceSource.TradingReferencePrice);
        return lastTradePrice is > 0m &&
            Update(symbol, lastTradePrice.Value, ReferencePriceSource.LastTradePrice);
    }

    public void OnSymbolDelisted(string symbol)
    {
        lock (_gate)
            _delisted.Add(symbol);
    }

    public void OnSubscriptionError(string symbol)
    {
        lock (_gate)
            GetOrAdd(symbol).SubscriptionErrorEpoch = _connectionEpoch;
    }

    /// <summary>
    /// Entering Connected starts a new epoch. Cached marks remain available to
    /// StaticRefPrice, but strict eligibility requires a fresh mark stamped in
    /// the new epoch.
    /// </summary>
    public bool SetConnected(bool connected)
    {
        lock (_gate)
        {
            if (_connected == connected)
                return false;
            _connected = connected;
            if (connected)
                _connectionEpoch++;
            return true;
        }
    }

    private bool Update(string symbol, decimal price, ReferencePriceSource source)
    {
        if (price <= 0m)
            return false;

        lock (_gate)
        {
            var state = GetOrAdd(symbol);
            state.LastValidMark = new MarketMark(
                price,
                _clock.GetUtcNow(),
                source,
                _connectionEpoch);
            state.SubscriptionErrorEpoch = null;
            return true;
        }
    }

    private SymbolReferenceState GetOrAdd(string symbol)
    {
        if (!_symbols.TryGetValue(symbol, out var state))
        {
            state = new SymbolReferenceState();
            _symbols.Add(symbol, state);
        }
        return state;
    }

    private sealed class SymbolReferenceState
    {
        public MarketMark? LastValidMark { get; set; }
        public long? SubscriptionErrorEpoch { get; set; }
    }

    public readonly record struct MarketMark(
        decimal Price,
        DateTimeOffset ObservedAtUtc,
        ReferencePriceSource Source = ReferencePriceSource.Trade,
        long ConnectionEpoch = 0);
}

public enum ReferencePriceSource
{
    Trade,
    TradingReferencePrice,
    LastTradePrice,
}

public enum FeedUnavailableReason
{
    None,
    Disconnected,
    AwaitingCurrentEpochReference,
    SubscriptionError,
    StaleReference,
}

public readonly record struct ReferenceAvailability(
    bool IsEligible,
    FeedUnavailableReason UnavailableReason,
    bool IsConnected,
    long ConnectionEpoch,
    MarketPriceTracker.MarketMark? LastValidMark,
    TimeSpan? ReferenceAge);
