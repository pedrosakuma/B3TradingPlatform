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
    private DateTimeOffset? _connectionStartedAtUtc;

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
            var age = _clock.GetUtcNow() - mark.ReceivedAtUtc;
            return age >= TimeSpan.Zero && age <= maxAge;
        }
    }

    /// <summary>
    /// Strict per-symbol readiness. Eligibility requires a connected socket, no
    /// subscription error in the current epoch, a valid update stamped with the
    /// current epoch, and an age inside <paramref name="maxAge"/>. An update
    /// received exactly at the epoch start and a mark whose age equals maxAge
    /// are eligible; timestamps ahead of the local clock are rejected.
    /// </summary>
    public ReferenceAvailability GetAvailability(string symbol, TimeSpan maxAge)
    {
        lock (_gate)
        {
            _symbols.TryGetValue(symbol, out var state);
            var currentEpochMark = state?.CurrentEpochMark;
            var mark = currentEpochMark ?? state?.LastValidMark;
            TimeSpan? age = mark is { } value ? _clock.GetUtcNow() - value.ReceivedAtUtc : null;
            var reason = !_connected
                ? FeedUnavailableReason.Disconnected
                : state?.SubscriptionErrorEpoch == _connectionEpoch
                    ? FeedUnavailableReason.SubscriptionError
                    : currentEpochMark is null ||
                        currentEpochMark.Value.ConnectionEpoch != _connectionEpoch
                        ? FeedUnavailableReason.AwaitingCurrentEpochReference
                        : age < TimeSpan.Zero || age > maxAge
                            ? FeedUnavailableReason.StaleReference
                            : FeedUnavailableReason.None;
            return new ReferenceAvailability(
                reason == FeedUnavailableReason.None,
                reason,
                _connected,
                _connectionEpoch,
                _connectionStartedAtUtc,
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
        OnTrade(symbol, price, _clock.GetUtcNow());

    public bool OnTrade(string symbol, decimal price, DateTimeOffset receivedAtUtc) =>
        Update(symbol, price, ReferencePriceSource.Trade, receivedAtUtc);

    /// <summary>
    /// Prefers the venue's TradingReferencePrice, falling back to
    /// LastTradePrice only when the former is absent.
    /// </summary>
    public bool OnInfoSnapshot(string symbol, decimal? tradingReferencePrice, decimal? lastTradePrice)
        => OnInfoSnapshot(
            symbol,
            tradingReferencePrice,
            lastTradePrice,
            _clock.GetUtcNow());

    public bool OnInfoSnapshot(
        string symbol,
        decimal? tradingReferencePrice,
        decimal? lastTradePrice,
        DateTimeOffset receivedAtUtc)
    {
        if (tradingReferencePrice is { } reference)
            return reference > 0m &&
                Update(
                    symbol,
                    reference,
                    ReferencePriceSource.TradingReferencePrice,
                    receivedAtUtc);
        return lastTradePrice is > 0m &&
            Update(
                symbol,
                lastTradePrice.Value,
                ReferencePriceSource.LastTradePrice,
                receivedAtUtc);
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
    public bool SetConnected(bool connected, DateTimeOffset? changedAtUtc = null)
    {
        lock (_gate)
        {
            if (_connected == connected)
                return false;
            _connected = connected;
            if (connected)
            {
                _connectionEpoch++;
                _connectionStartedAtUtc = changedAtUtc ?? _clock.GetUtcNow();
                foreach (var state in _symbols.Values)
                    state.CurrentEpochMark = null;
            }
            return true;
        }
    }

    private bool Update(
        string symbol,
        decimal price,
        ReferencePriceSource source,
        DateTimeOffset receivedAtUtc)
    {
        if (price <= 0m)
            return false;

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var belongsToCurrentEpoch = _connected &&
                _connectionStartedAtUtc is { } epochStart &&
                receivedAtUtc >= epochStart &&
                receivedAtUtc <= now;
            var mark = new MarketMark(
                price,
                receivedAtUtc,
                source,
                belongsToCurrentEpoch ? _connectionEpoch : 0);
            var state = GetOrAdd(symbol);
            state.LastValidMark = mark;
            if (belongsToCurrentEpoch &&
                (state.CurrentEpochMark is not { } current ||
                    mark.ReceivedAtUtc >= current.ReceivedAtUtc))
            {
                state.CurrentEpochMark = mark;
                state.SubscriptionErrorEpoch = null;
            }
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
        public MarketMark? CurrentEpochMark { get; set; }
        public long? SubscriptionErrorEpoch { get; set; }
    }

    public readonly record struct MarketMark(
        decimal Price,
        DateTimeOffset ReceivedAtUtc,
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
    DateTimeOffset? ConnectionStartedAtUtc,
    MarketPriceTracker.MarketMark? LastValidMark,
    TimeSpan? ReferenceAge);
