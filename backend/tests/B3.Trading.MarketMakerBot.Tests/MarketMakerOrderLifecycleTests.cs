using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketMakerOrderLifecycleTests
{
    [Fact]
    public async Task ReplayedFill_LookupPruneApplyInterleaving_CannotDoubleBook()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T03:00:00Z"));
        var tracker = new OrderTracker(clock);
        var ledger = new MarketMakerPnlLedger(clock);
        var lifecycle = new MarketMakerOrderLifecycle(tracker, ledger);
        const ulong clOrdId = 1;
        var fill = new OwnFill(
            clOrdId, 10, "PETR4", true, 100, 30m, 100, 100, 0, IsOrderFilled: true);
        Assert.True(tracker.TryRegisterSubmit(clOrdId, "PETR4", 30m, 100, isBuy: true));

        lifecycle.Synchronize(() =>
        {
            Assert.True(tracker.TryGet(clOrdId, out _));
            Assert.Equal(FillApplyStatus.Applied, ledger.Apply(fill).Status);
            tracker.OnTrade(clOrdId, isFilled: true, leaves: 0);
            ledger.MarkTerminal(clOrdId);
        });
        Assert.True(ledger.TryGetSnapshot("PETR4", out var beforeReplay));

        clock.Advance(TimeSpan.FromMinutes(6));
        var lookupCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pruneWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var replay = Task.Run(() => lifecycle.Synchronize(() =>
        {
            Assert.True(tracker.TryGet(clOrdId, out _));
            lookupCompleted.SetResult();
            allowApply.Task.GetAwaiter().GetResult();

            var result = ledger.Apply(fill);
            tracker.OnTrade(clOrdId, isFilled: true, leaves: 0);
            ledger.MarkTerminal(clOrdId);
            return result;
        }));

        await lookupCompleted.Task;
        var prune = Task.Run(() => lifecycle.Prune(
            TimeSpan.FromMinutes(5),
            () => pruneWaiting.SetResult()));
        await pruneWaiting.Task;

        allowApply.SetResult();
        var replayResult = await replay;
        await prune;

        Assert.Equal(FillApplyStatus.Duplicate, replayResult.Status);
        Assert.True(ledger.TryGetSnapshot("PETR4", out var afterReplay));
        Assert.Equal(beforeReplay, afterReplay);
        Assert.Equal(1, ledger.OrderStateCount);
    }
}
