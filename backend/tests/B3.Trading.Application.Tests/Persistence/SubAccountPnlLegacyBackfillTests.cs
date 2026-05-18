using System.Diagnostics.Metrics;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// PR #316 P1 (review). A pre-#316 snapshot has open positions but
/// NO <see cref="PlatformSnapshot.SubAccountPnlBasis"/> block. The
/// backfill must seed master basis from the aggregate
/// <see cref="PlatformSnapshot.Positions"/> ONLY when that aggregate
/// is master-only (no sub-position rows for the same key) — pre-#316
/// snapshots never carry sub positions, so the aggregate IS the
/// master-only basis for those. When sub positions are present
/// alongside an absent basis block, the aggregate row is the sum of
/// master + every sub bucket and its average is a cross-bucket
/// weighted average; seeding master from it would pollute master with
/// sibling-bucket basis. In that incoherent case the backfill leaves
/// master unseeded, fires
/// <c>trading.subaccount.master_basis_unrecoverable_total</c>, and
/// the statement endpoint's existing fail-closed path
/// (AvgPrice = 0 + <c>master_avg_basis_degraded_total</c>) surfaces
/// the gap.
/// </summary>
public class SubAccountPnlLegacyBackfillTests
{
    private const string Firm = "FIRM01";
    private const string Owner = "alice";

    [Fact]
    public void Restore_LegacyMasterOnlySnapshot_SeedsMasterBucketBasis()
    {
        // Pre-#316 snapshot shape: positions present, sub positions
        // empty, basis block empty. The aggregate row IS the master-
        // only basis (no sub fills ever existed in the pre-#316
        // world) — backfill must seed it.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 150, AverageEntryPrice: 30m, FirmId: Firm),
            },
        };

        var subAccountPnl = new SubAccountPnlKeeper();
        NewSnapshotter(subAccountPnl).Restore(snap);

        var master = subAccountPnl.GetBucketAvgCost(Firm, Owner, subAccount: null, "PETR4");
        Assert.NotNull(master);
        Assert.Equal(150, master!.NetQuantity);
        Assert.Equal(30m, master.AvgPrice);
    }

    [Fact]
    public void FirstCloseAfterRestore_MasterOnlySnapshot_RealizedMatchesLive()
    {
        // Live keeper builds master basis via a buy fill, then a
        // sell close establishes the baseline realised delta.
        var live = new SubAccountPnlKeeper();
        Assert.Equal(0m, live.ApplyBucketFill(Firm, Owner, null, "PETR4", OrderSide.Buy, 150, 30m));
        var liveClose = live.ApplyBucketFill(Firm, Owner, null, "PETR4", OrderSide.Sell, 60, 32m);
        Assert.NotEqual(0m, liveClose);

        // Restore from a master-only legacy snapshot (no basis
        // block) and replay the same close. The backfill must
        // reconstruct master basis from Positions so realized
        // matches.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 150, AverageEntryPrice: 30m, FirmId: Firm),
            },
        };
        var restored = new SubAccountPnlKeeper();
        NewSnapshotter(restored).Restore(snap);

        Assert.Equal(liveClose, restored.ApplyBucketFill(Firm, Owner, null, "PETR4", OrderSide.Sell, 60, 32m));
    }

    [Fact]
    public void Restore_IncoherentSnapshot_DoesNotSeedMaster_AndFiresUnrecoverableMetric()
    {
        // Sub positions present for the same (firm, owner, symbol)
        // as the master Positions row AND no SubAccountPnlBasis
        // block: the aggregate row is polluted (its avg blends
        // master + sub bucket history). Backfill MUST refuse to
        // seed master from the aggregate and surface the
        // unrecoverable metric.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 150, AverageEntryPrice: 31m, FirmId: Firm),
            },
            SubAccountPositions =
            {
                new SubAccountPositionSnapshot(Firm, Owner, "subA", "PETR4", NetQuantity: 50, AverageEntryPrice: 31m),
            },
        };

        var subAccountPnl = new SubAccountPnlKeeper();
        using var unrecoverable = new CounterCollector("trading.subaccount.master_basis_unrecoverable_total");
        NewSnapshotter(subAccountPnl).Restore(snap);

        // Master basis MUST be absent — seeding from the polluted
        // aggregate would silently realise wrong P&L on the next
        // close.
        Assert.Null(subAccountPnl.GetBucketAvgCost(Firm, Owner, subAccount: null, "PETR4"));
        // Sub bucket still seeded from its own (clean) avg.
        var subA = subAccountPnl.GetBucketAvgCost(Firm, Owner, new SubAccountId("subA"), "PETR4");
        Assert.NotNull(subA);
        Assert.Equal(50, subA!.NetQuantity);
        Assert.Equal(31m, subA.AvgPrice);

        // Metric fires once per (firm, owner, symbol).
        Assert.Equal(1, unrecoverable.Total);

        // Subsequent master close goes through ApplyBucketFill's
        // fresh-open branch and realises 0 — the consistent
        // fail-closed behaviour. The aggregate-pollution avg
        // (31m) is never imported.
        var realised = subAccountPnl.ApplyBucketFill(Firm, Owner, null, "PETR4", OrderSide.Sell, 50, 32m);
        Assert.Equal(0m, realised);
    }

    [Fact]
    public void Restore_IncoherentSnapshot_WithAuthoritativeBasis_BackfillIsNoOp()
    {
        // Sub positions present AND authoritative SubAccountPnlBasis
        // row present for the master bucket: the backfill must leave
        // the restored basis untouched (basis wins) and must NOT
        // fire the unrecoverable metric (the basis is recoverable
        // from the snapshot).
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 150, AverageEntryPrice: 31m, FirmId: Firm),
            },
            SubAccountPositions =
            {
                new SubAccountPositionSnapshot(Firm, Owner, "subA", "PETR4", NetQuantity: 50, AverageEntryPrice: 31m),
            },
            SubAccountPnlBasis =
            {
                new SubAccountPnlBasisSnapshot(Firm, Owner, SubAccountPnlKeeper.MasterBucketKey, "PETR4", NetQuantity: 100, AvgPrice: 27.5m),
            },
        };

        var subAccountPnl = new SubAccountPnlKeeper();
        using var unrecoverable = new CounterCollector("trading.subaccount.master_basis_unrecoverable_total");
        NewSnapshotter(subAccountPnl).Restore(snap);

        var master = subAccountPnl.GetBucketAvgCost(Firm, Owner, subAccount: null, "PETR4");
        Assert.NotNull(master);
        Assert.Equal(100, master!.NetQuantity);
        Assert.Equal(27.5m, master.AvgPrice);
        Assert.Equal(0, unrecoverable.Total);
    }

    [Fact]
    public void Restore_LegacySubPositions_SeedSubBucketBasis()
    {
        // Sub-bucket basis is recoverable directly from the
        // SubAccountPositionSnapshot rows — they're per-bucket by
        // construction, no cross-bucket pollution. Validate the
        // sub-bucket seed still happens even when the master row is
        // absent (e.g. flat master, long sub).
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            SubAccountPositions =
            {
                new SubAccountPositionSnapshot(Firm, Owner, "subA", "PETR4", NetQuantity: 50, AverageEntryPrice: 31m),
                new SubAccountPositionSnapshot(Firm, Owner, "subB", "PETR4", NetQuantity: -20, AverageEntryPrice: 33m),
            },
        };

        var subAccountPnl = new SubAccountPnlKeeper();
        NewSnapshotter(subAccountPnl).Restore(snap);

        var subA = subAccountPnl.GetBucketAvgCost(Firm, Owner, new SubAccountId("subA"), "PETR4");
        var subB = subAccountPnl.GetBucketAvgCost(Firm, Owner, new SubAccountId("subB"), "PETR4");
        Assert.NotNull(subA);
        Assert.Equal(50, subA!.NetQuantity);
        Assert.Equal(31m, subA.AvgPrice);
        Assert.NotNull(subB);
        Assert.Equal(-20, subB!.NetQuantity);
        Assert.Equal(33m, subB.AvgPrice);
    }

    [Fact]
    public void Restore_LegacyBasisAlreadyPresent_BackfillIsNoOp()
    {
        // Authoritative basis present for a master-only row: the
        // backfill TryAdd must be a no-op so the snapshot value
        // wins over the (otherwise-equivalent) seed path.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot(Owner, "PETR4", NetQuantity: 100, AverageEntryPrice: 30m, FirmId: Firm),
            },
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

    /// <summary>
    /// Lightweight MeterListener wrapper that subscribes to a single
    /// instrument by name and accumulates the long values it sees.
    /// </summary>
    private sealed class CounterCollector : IDisposable
    {
        private readonly MeterListener _listener;
        public long Total;

        public CounterCollector(string instrumentName)
        {
            _listener = new MeterListener();
            _listener.InstrumentPublished = (instr, listener) =>
            {
                if (instr.Name == instrumentName) listener.EnableMeasurementEvents(instr);
            };
            _listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref Total, m));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
