namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Synchronizes cross-component order lifecycle transitions that must observe
/// <see cref="OrderTracker"/> and <see cref="MarketMakerPnlLedger"/> as one
/// unit. Callbacks must remain synchronous; network/requote awaits happen only
/// after this boundary is released.
/// </summary>
internal sealed class MarketMakerOrderLifecycle
{
    private readonly object _gate = new();
    private readonly OrderTracker _tracker;
    private readonly MarketMakerPnlLedger _ledger;

    public MarketMakerOrderLifecycle(OrderTracker tracker, MarketMakerPnlLedger ledger)
    {
        _tracker = tracker;
        _ledger = ledger;
    }

    internal T Synchronize<T>(Func<T> transition)
    {
        lock (_gate)
            return transition();
    }

    internal void Synchronize(Action transition)
    {
        lock (_gate)
            transition();
    }

    internal void Prune(TimeSpan retention, Action? onWaitingForBoundary = null)
    {
        var lockTaken = Monitor.TryEnter(_gate);
        if (!lockTaken)
        {
            onWaitingForBoundary?.Invoke();
            Monitor.Enter(_gate);
            lockTaken = true;
        }

        try
        {
            var now = _tracker.UtcNow;
            _tracker.PruneClosed(retention, now);
            _ledger.PruneTerminal(retention, now);
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(_gate);
        }
    }
}
