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
}
