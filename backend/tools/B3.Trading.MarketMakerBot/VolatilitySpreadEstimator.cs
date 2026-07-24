using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Deterministic, bounded absolute trade-to-trade move estimator. State is
/// isolated per configured symbol and retained across feed disconnects, but
/// dynamic widening is served only while the feed is connected.
/// </summary>
public sealed class VolatilitySpreadEstimator
{
    private readonly IReadOnlyDictionary<string, InstrumentConfig> _instruments;
    private readonly Dictionary<string, SymbolState> _states;
    private readonly TimeProvider _clock;
    private volatile bool _connected;

    public VolatilitySpreadEstimator(IOptions<MarketMakerBotOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _instruments = options.Value.Instruments.ToDictionary(instrument => instrument.Symbol, StringComparer.Ordinal);
        _states = _instruments.Keys.ToDictionary(symbol => symbol, _ => new SymbolState(), StringComparer.Ordinal);
    }

    public VolatilitySpreadChange? OnTrade(string symbol, decimal price)
    {
        if (price <= 0m ||
            !_instruments.TryGetValue(symbol, out var instrument) ||
            !instrument.VolatilitySpread.Enabled ||
            instrument.TickSize <= 0m)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var state = _states[symbol];
        lock (state.Gate)
        {
            Prune(state, instrument.VolatilitySpread, now);
            if (state.PreviousTrade is { } previous)
            {
                try
                {
                    var moveTicks = Math.Abs(price - previous.Price) / instrument.TickSize;
                    var updatedSum = checked(state.SumMoveTicks + moveTicks);
                    state.Samples.Enqueue(new MoveSample(now, moveTicks));
                    state.SumMoveTicks = updatedSum;
                    while (state.Samples.Count > instrument.VolatilitySpread.MaxSamples)
                        RemoveOldest(state);
                }
                catch (OverflowException)
                {
                    // The trade remains a valid new baseline, but an
                    // unrepresentable move cannot be a deterministic sample.
                }
            }

            state.PreviousTrade = new TradeMark(now, price);
            return PublishChangeIfNeeded(symbol, state, instrument.VolatilitySpread);
        }
    }

    public IReadOnlyList<VolatilitySpreadChange> SetConnected(bool connected)
    {
        _connected = connected;
        return Refresh();
    }

    /// <summary>
    /// Prunes every configured symbol and reports only effective additional
    /// tick changes not already published by a trade/connection update.
    /// </summary>
    public IReadOnlyList<VolatilitySpreadChange> Refresh()
    {
        var now = _clock.GetUtcNow();
        var changes = new List<VolatilitySpreadChange>();
        foreach (var (symbol, instrument) in _instruments)
        {
            if (!instrument.VolatilitySpread.Enabled)
                continue;
            var state = _states[symbol];
            lock (state.Gate)
            {
                Prune(state, instrument.VolatilitySpread, now);
                if (PublishChangeIfNeeded(symbol, state, instrument.VolatilitySpread) is { } change)
                    changes.Add(change);
            }
        }
        return changes;
    }

    /// <summary>Reads and age-prunes one symbol without consuming a pending change notification.</summary>
    public VolatilitySpreadSnapshot GetSnapshot(string symbol)
    {
        if (!_instruments.TryGetValue(symbol, out var instrument) ||
            !instrument.VolatilitySpread.Enabled)
        {
            return VolatilitySpreadSnapshot.Disabled;
        }

        var state = _states[symbol];
        lock (state.Gate)
        {
            Prune(state, instrument.VolatilitySpread, _clock.GetUtcNow());
            return Snapshot(state, instrument.VolatilitySpread);
        }
    }

    private VolatilitySpreadChange? PublishChangeIfNeeded(
        string symbol,
        SymbolState state,
        VolatilitySpreadConfig config)
    {
        var snapshot = Snapshot(state, config);
        if (snapshot.AdditionalSpreadTicks == state.PublishedAdditionalTicks)
            return null;
        var previous = state.PublishedAdditionalTicks;
        state.PublishedAdditionalTicks = snapshot.AdditionalSpreadTicks;
        return new VolatilitySpreadChange(symbol, previous, snapshot);
    }

    private VolatilitySpreadSnapshot Snapshot(SymbolState state, VolatilitySpreadConfig config)
    {
        decimal? estimate = state.Samples.Count == 0
            ? null
            : state.SumMoveTicks / state.Samples.Count;
        var ready = state.Samples.Count >= config.MinSamples;
        var additionalTicks = _connected && ready && estimate is { } value
            ? CalculateAdditionalTicks(value, config)
            : 0;
        return new VolatilitySpreadSnapshot(
            estimate,
            additionalTicks,
            state.Samples.Count,
            ready,
            _connected,
            Enabled: true);
    }

    private static int CalculateAdditionalTicks(decimal estimate, VolatilitySpreadConfig config)
    {
        if (estimate <= 0m || config.MaxAdditionalSpreadTicks == 0)
            return 0;

        var cap = config.MaxAdditionalSpreadTicks;
        decimal scaled;
        try
        {
            scaled = checked(estimate * config.Multiplier);
        }
        catch (OverflowException)
        {
            return cap;
        }

        if (scaled == 0m)
            return config.Multiplier > 0m ? Math.Min(1, cap) : 0;
        if (scaled >= cap)
            return cap;
        scaled = Math.Ceiling(scaled);
        return decimal.ToInt32(decimal.Clamp(scaled, 0m, cap));
    }

    private static void Prune(SymbolState state, VolatilitySpreadConfig config, DateTimeOffset now)
    {
        while (state.Samples.TryPeek(out var sample) &&
               (sample.ObservedAtUtc > now || now - sample.ObservedAtUtc > config.Window))
        {
            RemoveOldest(state);
        }

        if (state.PreviousTrade is { } previous &&
            (previous.ObservedAtUtc > now || now - previous.ObservedAtUtc > config.Window))
        {
            state.PreviousTrade = null;
        }
    }

    private static void RemoveOldest(SymbolState state)
    {
        var removed = state.Samples.Dequeue();
        state.SumMoveTicks -= removed.MoveTicks;
    }

    private sealed class SymbolState
    {
        public object Gate { get; } = new();
        public Queue<MoveSample> Samples { get; } = new();
        public TradeMark? PreviousTrade { get; set; }
        public decimal SumMoveTicks { get; set; }
        public int PublishedAdditionalTicks { get; set; }
    }

    private readonly record struct MoveSample(DateTimeOffset ObservedAtUtc, decimal MoveTicks);
    private readonly record struct TradeMark(DateTimeOffset ObservedAtUtc, decimal Price);
}

public readonly record struct VolatilitySpreadSnapshot(
    decimal? MoveEstimateTicks,
    int AdditionalSpreadTicks,
    int SampleCount,
    bool IsReady,
    bool IsConnected,
    bool Enabled)
{
    public static VolatilitySpreadSnapshot Disabled => new(null, 0, 0, false, false, false);
}

public readonly record struct VolatilitySpreadChange(
    string Symbol,
    int PreviousAdditionalSpreadTicks,
    VolatilitySpreadSnapshot Current);
