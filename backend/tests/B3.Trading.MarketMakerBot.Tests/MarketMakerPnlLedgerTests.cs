using System.Collections.Concurrent;
using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketMakerPnlLedgerTests
{
    [Fact]
    public void Apply_LongIncreaseReductionAndFlatten_UsesWeightedAverageCost()
    {
        var ledger = new MarketMakerPnlLedger();

        AssertApplied(ledger, Fill(1, 1, isBuy: true, quantity: 10, price: 100m));
        AssertApplied(ledger, Fill(2, 2, isBuy: true, quantity: 10, price: 120m));
        AssertSnapshot(ledger, position: 20, averageCost: 110m, realizedPnl: 0m);

        AssertApplied(ledger, Fill(3, 3, isBuy: false, quantity: 5, price: 130m));
        AssertSnapshot(ledger, position: 15, averageCost: 110m, realizedPnl: 100m);

        AssertApplied(ledger, Fill(4, 4, isBuy: false, quantity: 15, price: 100m));
        AssertSnapshot(ledger, position: 0, averageCost: 0m, realizedPnl: -50m);
    }

    [Fact]
    public void Apply_LongToShortReversal_RealizesClosedQuantityAndResetsCost()
    {
        var ledger = new MarketMakerPnlLedger();

        AssertApplied(ledger, Fill(1, 1, isBuy: true, quantity: 10, price: 100m));
        AssertApplied(ledger, Fill(2, 2, isBuy: false, quantity: 15, price: 120m));

        AssertSnapshot(ledger, position: -5, averageCost: 120m, realizedPnl: 200m);
    }

    [Fact]
    public void Apply_ShortIncreaseReductionAndReversal_UsesWeightedAverageCost()
    {
        var ledger = new MarketMakerPnlLedger();

        AssertApplied(ledger, Fill(1, 1, isBuy: false, quantity: 10, price: 100m));
        AssertApplied(ledger, Fill(2, 2, isBuy: false, quantity: 10, price: 80m));
        AssertSnapshot(ledger, position: -20, averageCost: 90m, realizedPnl: 0m);

        AssertApplied(ledger, Fill(3, 3, isBuy: true, quantity: 5, price: 70m));
        AssertApplied(ledger, Fill(4, 4, isBuy: true, quantity: 20, price: 110m));

        AssertSnapshot(ledger, position: 5, averageCost: 110m, realizedPnl: -200m);
    }

    [Fact]
    public void Apply_ValidCumulativePartialThenFull_AppliesBothOnce()
    {
        var ledger = new MarketMakerPnlLedger();

        AssertApplied(ledger, Fill(1, 1, true, 40, 100m, orderQuantity: 100,
            cumQty: 40, leavesQty: 60, isFilled: false));
        AssertApplied(ledger, Fill(1, 2, true, 60, 110m, orderQuantity: 100,
            cumQty: 100, leavesQty: 0, isFilled: true));

        AssertSnapshot(ledger, position: 100, averageCost: 106m, realizedPnl: 0m);
    }

    [Fact]
    public void Apply_FirstObservedFillWithForwardCumQty_BooksCumulativeDeltaAndFlagsMismatch()
    {
        var ledger = new MarketMakerPnlLedger();

        var result = ledger.Apply(Fill(1, 1, true, 20, 30m, orderQuantity: 100,
            cumQty: 60, leavesQty: 40, isFilled: false));

        Assert.Equal(FillApplyStatus.Applied, result.Status);
        Assert.True(result.QuantityMismatch);
        Assert.Equal((ulong)60, result.BookedQuantity);
        AssertSnapshot(ledger, position: 60, averageCost: 30m, realizedPnl: 0m);
    }

    [Fact]
    public void Apply_MissedIntermediateExecution_BooksForwardCumulativeJumpAtObservedPrice()
    {
        var ledger = new MarketMakerPnlLedger();
        AssertApplied(ledger, Fill(1, 1, true, 20, 100m, orderQuantity: 100,
            cumQty: 20, leavesQty: 80, isFilled: false));

        var result = ledger.Apply(Fill(1, 3, true, 30, 110m, orderQuantity: 100,
            cumQty: 100, leavesQty: 0, isFilled: true));

        Assert.Equal(FillApplyStatus.Applied, result.Status);
        Assert.True(result.QuantityMismatch);
        Assert.Equal((ulong)80, result.BookedQuantity);
        AssertSnapshot(ledger, position: 100, averageCost: 108m, realizedPnl: 0m);
    }

    [Fact]
    public void Apply_StaleCumulativeQuantityWithNewExecutionIdentity_IsDeduplicated()
    {
        var ledger = new MarketMakerPnlLedger();
        AssertApplied(ledger, Fill(1, 1, true, 40, 30m, orderQuantity: 100,
            cumQty: 40, leavesQty: 60, isFilled: false));

        var result = ledger.Apply(Fill(1, 2, true, 40, 30m, orderQuantity: 100,
            cumQty: 40, leavesQty: 60, isFilled: false));

        Assert.Equal(FillApplyStatus.Duplicate, result.Status);
        AssertSnapshot(ledger, position: 40, averageCost: 30m, realizedPnl: 0m);
    }

    [Fact]
    public void Apply_ForwardCumulativeQuantityBeyondKnownOrder_IsRejected()
    {
        var ledger = new MarketMakerPnlLedger();

        var result = ledger.Apply(Fill(1, 1, true, 10, 30m, orderQuantity: 100,
            cumQty: 101, leavesQty: 0, isFilled: true));

        Assert.Equal(FillApplyStatus.Inconsistent, result.Status);
        Assert.False(ledger.TryGetSnapshot("PETR4", out _));
    }

    [Fact]
    public void Apply_DuplicateExecution_IsIdempotent()
    {
        var ledger = new MarketMakerPnlLedger();
        var fill = Fill(1, 10, true, 100, 30m);

        AssertApplied(ledger, fill);
        var duplicate = ledger.Apply(fill);

        Assert.Equal(FillApplyStatus.Duplicate, duplicate.Status);
        AssertSnapshot(ledger, position: 100, averageCost: 30m, realizedPnl: 0m);
    }

    [Fact]
    public void Apply_ReusedExecutionIdentityWithDifferentPayload_IsRejected()
    {
        var ledger = new MarketMakerPnlLedger();
        AssertApplied(ledger, Fill(1, 10, true, 100, 30m));

        var result = ledger.Apply(Fill(1, 10, true, 100, 31m));

        Assert.Equal(FillApplyStatus.Inconsistent, result.Status);
        AssertSnapshot(ledger, position: 100, averageCost: 30m, realizedPnl: 0m);
    }

    [Theory]
    [InlineData(100, 60, 100, 0, false)]
    [InlineData(40, 40, 100, 50, false)]
    [InlineData(40, 40, 100, 60, true)]
    public void Apply_InconsistentCumulativeStatusOrLeaves_DoesNotMutate(
        ulong cumQty, ulong lastQty, long orderQuantity, ulong leavesQty, bool isFilled)
    {
        var ledger = new MarketMakerPnlLedger();

        var result = ledger.Apply(Fill(1, 1, true, lastQty, 30m, orderQuantity,
            cumQty, leavesQty, isFilled));

        Assert.Equal(FillApplyStatus.Inconsistent, result.Status);
        Assert.False(ledger.TryGetSnapshot("PETR4", out _));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void Apply_InvalidQuantityOrPrice_DoesNotMutate(ulong quantity, decimal price)
    {
        var ledger = new MarketMakerPnlLedger();

        var result = ledger.Apply(Fill(1, 1, true, quantity, price));

        Assert.Equal(FillApplyStatus.Invalid, result.Status);
        Assert.False(ledger.TryGetSnapshot("PETR4", out _));
    }

    [Fact]
    public void Apply_InvalidOrderStatus_DoesNotMutate()
    {
        var ledger = new MarketMakerPnlLedger();
        var fill = Fill(1, 1, true, 100, 30m) with { HasValidOrderStatus = false };

        var result = ledger.Apply(fill);

        Assert.Equal(FillApplyStatus.Invalid, result.Status);
        Assert.False(ledger.TryGetSnapshot("PETR4", out _));
    }

    [Fact]
    public void Apply_IsThreadSafeAcrossDistinctExecutions()
    {
        var ledger = new MarketMakerPnlLedger();
        var results = new ConcurrentBag<FillApplyStatus>();

        Parallel.For(1, 101, tradeId =>
        {
            results.Add(ledger.Apply(Fill(1, (ulong)tradeId, true, 1, 30m,
                orderQuantity: 1000, isFilled: false)).Status);
        });

        Assert.All(results, status => Assert.Equal(FillApplyStatus.Applied, status));
        AssertSnapshot(ledger, position: 100, averageCost: 30m, realizedPnl: 0m);
    }

    [Fact]
    public void Snapshot_IncludesProcessAccountingPeriodStart()
    {
        var startedAt = DateTimeOffset.Parse("2026-07-24T03:00:00Z");
        var ledger = new MarketMakerPnlLedger(new ManualTimeProvider(startedAt));
        AssertApplied(ledger, Fill(1, 1, true, 100, 30m));

        Assert.True(ledger.TryGetSnapshot("PETR4", out var snapshot));
        Assert.Equal(startedAt, snapshot.AccountingPeriodStartedAtUtc);
    }

    [Fact]
    public void PruneTerminal_RetainsActiveOrderAccountingState()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T03:00:00Z"));
        var ledger = new MarketMakerPnlLedger(clock);
        var fill = Fill(1, 1, true, 40, 30m, orderQuantity: 100,
            cumQty: 40, leavesQty: 60, isFilled: false);
        AssertApplied(ledger, fill);

        clock.Advance(TimeSpan.FromHours(1));
        ledger.PruneTerminal(TimeSpan.FromMinutes(5), clock.GetUtcNow());

        Assert.Equal(1, ledger.OrderStateCount);
        Assert.Equal(FillApplyStatus.Duplicate, ledger.Apply(fill).Status);
        AssertSnapshot(ledger, position: 40, averageCost: 30m, realizedPnl: 0m);
    }

    [Fact]
    public void PruneTerminal_RetainsRecentTerminalReplayDedupWindow()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T03:00:00Z"));
        var ledger = new MarketMakerPnlLedger(clock);
        var fill = Fill(1, 1, true, 100, 30m);
        AssertApplied(ledger, fill);
        ledger.MarkTerminal(1);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(FillApplyStatus.Duplicate, ledger.Apply(fill).Status);
        clock.Advance(TimeSpan.FromMinutes(2));
        ledger.PruneTerminal(TimeSpan.FromMinutes(5), clock.GetUtcNow());

        Assert.Equal(1, ledger.OrderStateCount);
        Assert.Equal(FillApplyStatus.Duplicate, ledger.Apply(fill).Status);
        AssertSnapshot(ledger, position: 100, averageCost: 30m, realizedPnl: 0m);
    }

    [Fact]
    public void PruneTerminal_EvictsExpiredStateWithoutChangingPositionOrPnl()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T03:00:00Z"));
        var ledger = new MarketMakerPnlLedger(clock);
        AssertApplied(ledger, Fill(1, 1, true, 100, 30m));
        ledger.MarkTerminal(1);
        Assert.True(ledger.TryGetSnapshot("PETR4", out var before));

        clock.Advance(TimeSpan.FromMinutes(6));
        ledger.PruneTerminal(TimeSpan.FromMinutes(5), clock.GetUtcNow());

        Assert.Equal(0, ledger.OrderStateCount);
        Assert.True(ledger.TryGetSnapshot("PETR4", out var after));
        Assert.Equal(before, after);
    }

    private static OwnFill Fill(
        ulong clOrdId,
        ulong tradeId,
        bool isBuy,
        ulong quantity,
        decimal price,
        long? orderQuantity = null,
        ulong? cumQty = null,
        ulong? leavesQty = null,
        bool isFilled = true) =>
        new(clOrdId, tradeId, "PETR4", isBuy, orderQuantity ?? checked((long)quantity),
            price, quantity, cumQty, leavesQty, isFilled);

    private static void AssertApplied(MarketMakerPnlLedger ledger, OwnFill fill) =>
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(fill).Status);

    private static void AssertSnapshot(
        MarketMakerPnlLedger ledger,
        long position,
        decimal averageCost,
        decimal realizedPnl)
    {
        Assert.True(ledger.TryGetSnapshot("PETR4", out var snapshot));
        Assert.Equal(position, snapshot.Position);
        Assert.Equal(averageCost, snapshot.AverageCost);
        Assert.Equal(realizedPnl, snapshot.RealizedPnl);
    }
}
