using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// PR #316 P1 (review). A pre-#316 snapshot has open positions but
/// NO <see cref="PlatformSnapshot.SubAccountPnlBasis"/> block. Without
/// the legacy backfill, the first closing fill on a restored bucket
/// goes through <see cref="SubAccountPnlKeeper.ApplyBucketFill"/>'s
/// missing-basis branch and silently realises 0 — even though a
/// non-restarted host would have computed the realized delta against
/// the existing basis.
/// </summary>
public class SubAccountPnlLegacyBackfillTests
{
    private const string Firm = "FIRM01";
    private const string Owner = "alice";

    [Fact]
    public void Restore_LegacySnapshotWithoutBasis_SeedsMasterAndSubBucketBasis()
    {
        // Synthesize a pre-#316 snapshot: positions + sub-positions
        // present, but SubAccountPnlBasis intentionally empty.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 100, AverageEntryPrice: 30m, FirmId: Firm),
            },
            SubAccountPositions =
            {
                new SubAccountPositionSnapshot(Firm, Owner, "subA", "PETR4", NetQuantity: 50, AverageEntryPrice: 31m),
                new SubAccountPositionSnapshot(Firm, Owner, "subB", "PETR4", NetQuantity: -20, AverageEntryPrice: 33m),
            },
            // SubAccountPnlBasis intentionally empty.
        };

        var subAccountPnl = new SubAccountPnlKeeper();
        var snapshotter = NewSnapshotter(subAccountPnl);
        snapshotter.Restore(snap);

        // All three buckets must have a basis after restore.
        var master = subAccountPnl.GetBucketAvgCost(Firm, Owner, subAccount: null, "PETR4");
        var subA = subAccountPnl.GetBucketAvgCost(Firm, Owner, new SubAccountId("subA"), "PETR4");
        var subB = subAccountPnl.GetBucketAvgCost(Firm, Owner, new SubAccountId("subB"), "PETR4");

        Assert.NotNull(master);
        Assert.Equal(100, master!.NetQuantity);
        Assert.Equal(30m, master.AvgPrice);

        Assert.NotNull(subA);
        Assert.Equal(50, subA!.NetQuantity);
        Assert.Equal(31m, subA.AvgPrice);

        Assert.NotNull(subB);
        Assert.Equal(-20, subB!.NetQuantity);
        Assert.Equal(33m, subB.AvgPrice);
    }

    [Fact]
    public void FirstCloseAfterRestore_ComputesRealizedIdenticalToLiveWithoutRestart()
    {
        // Live keeper builds the bucket history then takes a snapshot
        // that DROPS the basis block (simulating a pre-#316 snapshot
        // shape).
        var live = new SubAccountPnlKeeper();
        var subA = new SubAccountId("subA");
        var subB = new SubAccountId("subB");
        Assert.Equal(0m, live.ApplyBucketFill(Firm, Owner, null, "PETR4", OrderSide.Buy, 100, 30m));
        Assert.Equal(0m, live.ApplyBucketFill(Firm, Owner, subA, "PETR4", OrderSide.Buy, 50, 31m));
        Assert.Equal(0m, live.ApplyBucketFill(Firm, Owner, subB, "PETR4", OrderSide.Sell, 20, 33m));

        // Live realised on the next close — baseline.
        var liveMasterClose = live.ApplyBucketFill(Firm, Owner, null, "PETR4", OrderSide.Sell, 40, 32m);
        var liveSubAClose = live.ApplyBucketFill(Firm, Owner, subA, "PETR4", OrderSide.Sell, 20, 32m);
        var liveSubBClose = live.ApplyBucketFill(Firm, Owner, subB, "PETR4", OrderSide.Buy, 10, 30m);

        // Now restore from a legacy-shaped snapshot (positions only,
        // no basis block) and replay the same closes.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 100, AverageEntryPrice: 30m, FirmId: Firm),
            },
            SubAccountPositions =
            {
                new SubAccountPositionSnapshot(Firm, Owner, "subA", "PETR4", NetQuantity: 50, AverageEntryPrice: 31m),
                new SubAccountPositionSnapshot(Firm, Owner, "subB", "PETR4", NetQuantity: -20, AverageEntryPrice: 33m),
            },
        };

        var restored = new SubAccountPnlKeeper();
        NewSnapshotter(restored).Restore(snap);

        Assert.Equal(liveMasterClose, restored.ApplyBucketFill(Firm, Owner, null, "PETR4", OrderSide.Sell, 40, 32m));
        Assert.Equal(liveSubAClose, restored.ApplyBucketFill(Firm, Owner, subA, "PETR4", OrderSide.Sell, 20, 32m));
        Assert.Equal(liveSubBClose, restored.ApplyBucketFill(Firm, Owner, subB, "PETR4", OrderSide.Buy, 10, 30m));

        // And — the bug we're fixing — the master close MUST NOT realise 0.
        Assert.NotEqual(0m, liveMasterClose);
    }

    [Fact]
    public void Backfill_IsIdempotent_DoesNotOverwriteExistingBasis()
    {
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 100, AverageEntryPrice: 30m, FirmId: Firm),
            },
            // Authoritative basis block already present — backfill
            // must not clobber it with the legacy seed.
            SubAccountPnlBasis =
            {
                new SubAccountPnlBasisSnapshot(Firm, Owner, SubAccountPnlKeeper.MasterBucketKey, "PETR4", NetQuantity: 100, AvgPrice: 27.5m),
            },
        };

        var subAccountPnl = new SubAccountPnlKeeper();
        NewSnapshotter(subAccountPnl).Restore(snap);

        var master = subAccountPnl.GetBucketAvgCost(Firm, Owner, subAccount: null, "PETR4");
        Assert.NotNull(master);
        Assert.Equal(27.5m, master!.AvgPrice);
    }

    [Fact]
    public void Backfill_SkipsZeroAvgRow_LeavesBucketUnseeded()
    {
        // Pre-position-basis legacy row (zero avg). Cannot recover a
        // real basis — skip rather than seed phantom zero.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "VALE3", NetQuantity: 75, AverageEntryPrice: 0m, FirmId: Firm),
            },
        };

        var subAccountPnl = new SubAccountPnlKeeper();
        NewSnapshotter(subAccountPnl).Restore(snap);

        Assert.Null(subAccountPnl.GetBucketAvgCost(Firm, Owner, subAccount: null, "VALE3"));
    }

    private static StateSnapshotter NewSnapshotter(SubAccountPnlKeeper subAccountPnl) =>
        new(
            new WorkingOrderBook(),
            new PositionKeeper(),
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            new AlgoBook(),
            new AlgoIdRegistry(),
            new CashLedger(),
            subAccountPositions: new SubAccountPositionKeeper(),
            subAccountPnl: subAccountPnl);
}
