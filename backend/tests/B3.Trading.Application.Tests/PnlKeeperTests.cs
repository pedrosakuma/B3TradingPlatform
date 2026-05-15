using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q2.4 (#271). Unit-level coverage for <see cref="PnlKeeper"/>.
/// End-to-end snapshot+replay coverage lives in
/// <c>PnlKeeperRecoveryTests</c>; ER integration in
/// <c>ExecutionReportProcessorPnlTests</c>.
/// </summary>
public class PnlKeeperTests
{
    private static readonly DateOnly Day = new(2025, 1, 15);
    private static readonly DateTimeOffset Ts = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static RealizedPnlEvent Evt(ulong clOrdId, ulong cumQty, string ec, string sym, decimal delta, decimal running, DateOnly day)
        => new()
        {
            ClOrdId = clOrdId,
            ExecutionId = clOrdId + ":" + cumQty,
            EndClientId = ec,
            Symbol = sym,
            DayKey = day,
            DeltaRealized = delta,
            RunningTotal = running,
            TimestampUtc = Ts,
        };

    [Fact]
    public void Buy_Then_Buy_GrowsAvg_NoRealized()
    {
        var k = new PnlKeeper();
        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 100, 30m));
        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 100, 32m));
        var s = k.GetAvgCost("alice", "PETR4");
        Assert.NotNull(s);
        Assert.Equal(200, s!.NetQuantity);
        Assert.Equal(31m, s.AvgPrice);
    }

    [Fact]
    public void Buy_Then_Sell_PartialClose_RealizesAtSpread_AvgUnchanged()
    {
        var k = new PnlKeeper();
        k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 100, 30m);
        var realized = k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 50, 31m);
        Assert.Equal(50m, realized); // (31-30)*50
        var s = k.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(50, s.NetQuantity);
        Assert.Equal(30m, s.AvgPrice);
    }

    [Fact]
    public void Buy_Then_Sell_FlipThroughZero_ResetsAvgToFillPrice()
    {
        var k = new PnlKeeper();
        k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 100, 30m);
        // Sell 150 @ 31 → close 100 @ +1 = 100; remaining 50 opens short @ 31.
        var realized = k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 150, 31m);
        Assert.Equal(100m, realized);
        var s = k.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(-50, s.NetQuantity);
        Assert.Equal(31m, s.AvgPrice);
    }

    [Fact]
    public void Short_ClosedByBuy_RealizesNegativeSpread()
    {
        var k = new PnlKeeper();
        k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 100, 30m);
        // Buy back 50 @ 28 → short profit (30-28)*50 = 100.
        var realized = k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 50, 28m);
        Assert.Equal(100m, realized);
        var s = k.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(-50, s.NetQuantity);
        Assert.Equal(30m, s.AvgPrice);
    }

    [Fact]
    public void ComputeRealizedDelta_ZeroPosition_ReturnsZero()
        => Assert.Equal(0m, PnlKeeper.ComputeRealizedDelta(0, 0m, OrderSide.Sell, 50, 30m));

    [Fact]
    public void ComputeRealizedDelta_SameSide_ReturnsZero()
        => Assert.Equal(0m, PnlKeeper.ComputeRealizedDelta(100, 30m, OrderSide.Buy, 50, 31m));

    [Fact]
    public void Apply_AdvancesDayTotal()
    {
        var k = new PnlKeeper();
        Assert.True(k.Apply(Evt(1, 50, "alice", "PETR4", 50m, 50m, Day)));
        Assert.True(k.Apply(Evt(2, 50, "alice", "PETR4", 30m, 80m, Day)));
        Assert.Equal(80m, k.GetDayRealized("alice", "PETR4", Day));
        Assert.Equal(80m, k.GetDayRealizedTotal("alice", Day));
    }

    [Fact]
    public void Apply_CrossDay_BucketsSeparately()
    {
        var k = new PnlKeeper();
        var d2 = new DateOnly(2025, 1, 16);
        k.Apply(Evt(1, 50, "alice", "PETR4", 50m, 50m, Day));
        k.Apply(Evt(2, 50, "alice", "PETR4", 7m, 7m, d2));
        Assert.Equal(50m, k.GetDayRealized("alice", "PETR4", Day));
        Assert.Equal(7m, k.GetDayRealized("alice", "PETR4", d2));
    }

    [Fact]
    public void Apply_CrossSymbol_BucketsSeparately()
    {
        var k = new PnlKeeper();
        k.Apply(Evt(1, 50, "alice", "PETR4", 50m, 50m, Day));
        k.Apply(Evt(2, 50, "alice", "VALE3", 30m, 30m, Day));
        Assert.Equal(50m, k.GetDayRealized("alice", "PETR4", Day));
        Assert.Equal(30m, k.GetDayRealized("alice", "VALE3", Day));
        Assert.Equal(80m, k.GetDayRealizedTotal("alice", Day));
    }

    [Fact]
    public void Apply_DuplicateExecutionId_Idempotent()
    {
        var k = new PnlKeeper();
        var e = Evt(1, 50, "alice", "PETR4", 50m, 50m, Day);
        Assert.True(k.Apply(e));
        Assert.False(k.Apply(e));
        Assert.Equal(50m, k.GetDayRealized("alice", "PETR4", Day));
    }

    [Fact]
    public void Apply_UsesRunningTotal_NotDelta()
    {
        // Snapshot+tail recovery: the snapshot baked in 100 already,
        // and the tail event reports running=130 / delta=30. After
        // Apply we must report 130, not 100+30 (would be the same here
        // by coincidence) — and not "previous + delta" if previous
        // started from a different baseline.
        var k = new PnlKeeper();
        // Pretend snapshot restored 200.
        k.Restore(
            new Dictionary<string, decimal> { [PnlKeeper.FormatRealizedKey("alice", "PETR4", Day)] = 200m },
            Array.Empty<PnlAvgCostSnapshot>());
        // Tail reapplies an event whose running is the authoritative 130.
        Assert.True(k.Apply(Evt(1, 50, "alice", "PETR4", 30m, 130m, Day)));
        Assert.Equal(130m, k.GetDayRealized("alice", "PETR4", Day));
    }

    [Fact]
    public void Snapshot_Restore_RoundTrips()
    {
        var src = new PnlKeeper();
        src.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 100, 30m);
        src.ApplyFillToAvgCost("alice", "VALE3", OrderSide.Sell, 50, 60m);
        src.Apply(Evt(1, 50, "alice", "PETR4", 50m, 50m, Day));

        var realized = src.RawSnapshotRealized();
        var avg = src.RawSnapshotAvgCost();
        var seen = src.RawSnapshotSeenIds();
        var dict = realized.ToDictionary(
            r => PnlKeeper.FormatRealizedKey(r.EndClientId, r.Symbol, r.Day),
            r => r.Realized);
        var avgList = avg.Select(a => new PnlAvgCostSnapshot(a.EndClientId, a.Symbol, a.NetQuantity, a.AvgPrice));

        var dst = new PnlKeeper();
        dst.Restore(dict, avgList, seen);

        Assert.Equal(50m, dst.GetDayRealized("alice", "PETR4", Day));
        var ap = dst.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(100, ap.NetQuantity);
        Assert.Equal(30m, ap.AvgPrice);
        var av = dst.GetAvgCost("alice", "VALE3")!;
        Assert.Equal(-50, av.NetQuantity);
        Assert.Equal(60m, av.AvgPrice);
        // Seen-set survived.
        Assert.False(dst.Apply(Evt(1, 50, "alice", "PETR4", 50m, 50m, Day)));
    }

    [Fact]
    public void RawSnapshotAvgCost_SkipsFlat()
    {
        var k = new PnlKeeper();
        k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 100, 30m);
        k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 100, 31m);
        Assert.Empty(k.RawSnapshotAvgCost());
    }

    [Fact]
    public void FormatRealizedKey_ParsesBack()
    {
        var key = PnlKeeper.FormatRealizedKey("alice", "PETR4", Day);
        Assert.True(PnlKeeper.TryParseRealizedKey(key, out var ec, out var sym, out var d));
        Assert.Equal("alice", ec);
        Assert.Equal("PETR4", sym);
        Assert.Equal(Day, d);
    }

    [Fact]
    public void FinalizeReplay_NoSurvivors_WhenAllReconciled()
    {
        var k = new PnlKeeper();
        k.RegisterPendingReplaySynth("1:50", "alice", "PETR4", OrderSide.Sell, 50, 31m, Ts, 100, 30m);
        // A durable event matching the same execution arrives in WAL drain.
        k.Apply(Evt(1, 50, "alice", "PETR4", 50m, 50m, Day));
        Assert.Equal(0, k.FinalizeReplay());
        Assert.Equal(50m, k.GetDayRealized("alice", "PETR4", Day));
    }

    [Fact]
    public void FinalizeReplay_MaterialisesSurvivors_FromPreFillSnapshot()
    {
        var k = new PnlKeeper();
        k.RegisterPendingReplaySynth("1:50", "alice", "PETR4", OrderSide.Sell, 50, 31m, Ts, 100, 30m);
        Assert.Equal(1, k.FinalizeReplay());
        Assert.Equal(50m, k.GetDayRealized("alice", "PETR4", Day));
        // Idempotent: surviving execution id is now in the seen-set.
        Assert.False(k.Apply(Evt(1, 50, "alice", "PETR4", 50m, 50m, Day)));
    }

    [Fact]
    public void SeedAvgCostFromLegacyPositions_FillsBasis_WhenPnlAvgCostEmpty()
    {
        // Pass-1 review (#278) P1#1. Legacy snapshot scenario:
        // PnlAvgCost block is empty (pre-#271 snapshot) but Positions
        // has rows. The next sell on PETR4 must realise against the
        // basis carried by PositionSnapshot.AverageEntryPrice.
        var k = new PnlKeeper();
        k.Restore(
            new Dictionary<string, decimal>(),
            Array.Empty<PnlAvgCostSnapshot>());
        var positions = new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 30m),
            new PositionSnapshot("bob", "VALE3", -50, 60m),
            new PositionSnapshot("carol", "ITSA4", 0, 0m), // flat skipped
        };

        var seeded = k.SeedAvgCostFromLegacyPositions(positions);
        Assert.Equal(2, seeded);

        var ap = k.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(100, ap.NetQuantity);
        Assert.Equal(30m, ap.AvgPrice);
        var bp = k.GetAvgCost("bob", "VALE3")!;
        Assert.Equal(-50, bp.NetQuantity);
        Assert.Equal(60m, bp.AvgPrice);
        Assert.Null(k.GetAvgCost("carol", "ITSA4"));

        // Selling 50 of PETR4 @ 31 must now realise the (31-30)*50 = 50
        // spread against the seeded basis — without the seed the
        // realised value would be silently zero.
        var realized = k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 50, 31m);
        Assert.Equal(50m, realized);
    }

    [Fact]
    public void SeedAvgCostFromLegacyPositions_PreservesExistingPnlAvgCost()
    {
        // Snapshot already has PnlAvgCost rows — the legacy seed must
        // be a no-op for those keys (PnlKeeper's own block is
        // authoritative when present).
        var k = new PnlKeeper();
        k.Restore(
            new Dictionary<string, decimal>(),
            new[] { new PnlAvgCostSnapshot("alice", "PETR4", 100, 25m) });

        var seeded = k.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 30m), // mismatched basis
            new PositionSnapshot("alice", "VALE3", 200, 40m), // not in PnlAvgCost
        });

        Assert.Equal(1, seeded);
        Assert.Equal(25m, k.GetAvgCost("alice", "PETR4")!.AvgPrice);
        Assert.Equal(40m, k.GetAvgCost("alice", "VALE3")!.AvgPrice);
    }

    [Fact]
    public async Task ApplyFillUnderLock_SerialisesConcurrentFillsForSameKey()
    {
        // Pass-1 review (#278) P1#2. Two concurrent fills for the
        // same (endClient, symbol) must not race the running-total
        // computation. The per-key lock guarantees that the realized
        // delta and running total observed by each callback are
        // consistent with the in-memory state visible at the time of
        // the lock acquisition.
        var k = new PnlKeeper();
        // Open 1000 @ 30 so both concurrent sells realise spread.
        k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 1000, 30m);

        var observed = new System.Collections.Concurrent.ConcurrentBag<(decimal Delta, decimal Running)>();
        var t1 = Task.Run(() => k.ApplyFillUnderLock(
            "alice", "PETR4", OrderSide.Sell, 100, 31m, Day,
            ctx =>
            {
                observed.Add((ctx.RealizedDelta, ctx.RunningTotal));
                k.Apply(new RealizedPnlEvent
                {
                    ClOrdId = 1,
                    ExecutionId = "1:100",
                    EndClientId = "alice",
                    Symbol = "PETR4",
                    DayKey = Day,
                    DeltaRealized = ctx.RealizedDelta,
                    RunningTotal = ctx.RunningTotal,
                    TimestampUtc = Ts,
                });
            }));
        var t2 = Task.Run(() => k.ApplyFillUnderLock(
            "alice", "PETR4", OrderSide.Sell, 100, 32m, Day,
            ctx =>
            {
                observed.Add((ctx.RealizedDelta, ctx.RunningTotal));
                k.Apply(new RealizedPnlEvent
                {
                    ClOrdId = 2,
                    ExecutionId = "2:100",
                    EndClientId = "alice",
                    Symbol = "PETR4",
                    DayKey = Day,
                    DeltaRealized = ctx.RealizedDelta,
                    RunningTotal = ctx.RunningTotal,
                    TimestampUtc = Ts,
                });
            }));
        await Task.WhenAll(t1, t2);

        // Realised: (31-30)*100 = 100 and (32-30)*100 = 200. The
        // observed (delta, running) pairs depend on lock acquisition
        // order, but the invariants are: sum of deltas = 300; the
        // larger running equals 300; the smaller running equals the
        // delta of the fill that won the lock first; the keeper's
        // final GetDayRealized must be 300.
        var ordered = observed.OrderBy(o => o.Running).ToArray();
        Assert.Equal(2, ordered.Length);
        Assert.Equal(ordered[0].Delta, ordered[0].Running); // first under lock
        Assert.Equal(300m, ordered[1].Running);
        Assert.Equal(300m, ordered[0].Delta + ordered[1].Delta);
        Assert.Contains(ordered, o => o.Delta == 100m);
        Assert.Contains(ordered, o => o.Delta == 200m);
        Assert.Equal(300m, k.GetDayRealized("alice", "PETR4", Day));
    }
}
