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
    public void SeedAvgCostFromLegacyPositions_SkipsZeroAvgPriceRows()
    {
        // Pass-2 review (#278) P1#2. A non-flat position row with a
        // zero AverageEntryPrice is degenerate: seeding it would
        // book the next sell as realized = sellPrice * qty against
        // a zero basis, surfacing phantom realized P&L on first
        // close after restore. Pass-3 (#278) tracks the qty as
        // "unknown basis" instead of dropping it on the floor —
        // GetAvgCost still returns null for the key (no usable
        // basis), but ApplyFillToAvgCost realises 0 against the
        // tracked unknown leg instead of opening a synthetic short
        // at the sell price.
        var k = new PnlKeeper();
        k.Restore(
            new Dictionary<string, decimal>(),
            Array.Empty<PnlAvgCostSnapshot>());
        var positions = new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 0m),  // zero basis, unknown
            new PositionSnapshot("bob", "VALE3", -50, 0m),    // zero basis, unknown
            new PositionSnapshot("carol", "ITSA4", 75, 12m),  // valid basis, seeded
        };

        var seeded = k.SeedAvgCostFromLegacyPositions(positions);
        Assert.Equal(1, seeded);
        Assert.Null(k.GetAvgCost("alice", "PETR4"));
        Assert.Null(k.GetAvgCost("bob", "VALE3"));
        Assert.Equal(100, k.GetUnknownBasisQty("alice", "PETR4"));
        Assert.Equal(-50, k.GetUnknownBasisQty("bob", "VALE3"));
        var c = k.GetAvgCost("carol", "ITSA4")!;
        Assert.Equal(75, c.NetQuantity);
        Assert.Equal(12m, c.AvgPrice);

        // The next sell on a tracked-unknown key realises 0 — it
        // closes against the unknown leg, not against an invented
        // basis. (Previously it would have opened a synthetic
        // short at the sell price and surfaced phantom P&L on
        // subsequent fills.)
        var realized = k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 50, 31m);
        Assert.Equal(0m, realized);
        Assert.Equal(0m, k.GetDayRealized("alice", "PETR4", Day));
        Assert.Equal(50, k.GetUnknownBasisQty("alice", "PETR4"));
        Assert.Null(k.GetAvgCost("alice", "PETR4"));
    }

    [Fact]
    public void LegacyZeroBasisSnapshot_FirstSell_RealizesZero_NotPhantom()
    {
        // Sanity guard for the Pass-3 (#278) P1 fix. A legacy
        // long position with no carried basis must NOT book any
        // realized P&L on the first close — it closes against the
        // unknown leg.
        var k = new PnlKeeper();
        k.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 0m),
        });

        var realized = k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 40, 31m);
        Assert.Equal(0m, realized);
        Assert.Equal(60, k.GetUnknownBasisQty("alice", "PETR4"));
    }

    [Fact]
    public void LegacyZeroBasisSnapshot_BuyAfterPartialSell_DoesNotRealizePhantom()
    {
        // Pass-3 (#278) P1 — sell partial → buy more → sell again.
        // All fills against the unknown leg realise 0; only after
        // the unknown leg goes flat does a real basis form, and
        // only after that real basis forms can a close realise a
        // non-zero spread.
        var k = new PnlKeeper();
        k.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 0m),
        });

        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 30, 32m));
        Assert.Equal(70, k.GetUnknownBasisQty("alice", "PETR4"));

        // Same-side add to the unknown leg — still unknown, still 0.
        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 20, 33m));
        Assert.Equal(90, k.GetUnknownBasisQty("alice", "PETR4"));

        // Sell back down — still unknown, still 0.
        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 40, 34m));
        Assert.Equal(50, k.GetUnknownBasisQty("alice", "PETR4"));

        Assert.Null(k.GetAvgCost("alice", "PETR4"));
        Assert.Equal(0m, k.GetDayRealized("alice", "PETR4", Day));
    }

    [Fact]
    public void LegacyZeroBasisSnapshot_FullCloseAndReopen_EstablishesBasisFromFirstFreshFill()
    {
        // Pass-3 (#278) P1 — close to flat, then a new buy → next
        // sell realises correctly using the fresh basis.
        var k = new PnlKeeper();
        k.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 0m),
        });

        // Close the unknown leg fully — realise 0.
        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 100, 31m));
        Assert.Equal(0, k.GetUnknownBasisQty("alice", "PETR4"));
        Assert.Null(k.GetAvgCost("alice", "PETR4"));

        // Fresh open at a known price.
        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 50, 28m));
        var s = k.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(50, s.NetQuantity);
        Assert.Equal(28m, s.AvgPrice);

        // Now a sell at 30 realises (30-28)*40 = 80 against the fresh basis.
        Assert.Equal(80m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 40, 30m));
    }

    [Fact]
    public void LegacyZeroBasisSnapshot_SignFlip_TreatsResidualAsFreshOpen()
    {
        // Pass-3 (#278) P1 — long 100 unknown basis + sell 150 →
        // realised 0 for the 100 closed against the unknown leg,
        // residual 50 short opens at the fill price as a KNOWN
        // basis. Next buy then realises against that fresh basis.
        var k = new PnlKeeper();
        k.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 0m),
        });

        Assert.Equal(0m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 150, 31m));
        Assert.Equal(0, k.GetUnknownBasisQty("alice", "PETR4"));
        var s = k.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(-50, s.NetQuantity);
        Assert.Equal(31m, s.AvgPrice);

        // Buy back the 50 short @ 29 → short profit (31-29)*50 = 100.
        Assert.Equal(100m, k.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 50, 29m));
    }

    [Fact]
    public void UnknownBasis_RoundTripsThroughSnapshotRestore()
    {
        // Pass-3 (#278) P1 — the unknown-basis set must be
        // persisted alongside the avg-cost block so a snapshot+tail
        // recovery doesn't re-skip and re-introduce the phantom-
        // P&L bug on every restart. Mid-recovery state must match
        // a single-pass recovery.
        var src = new PnlKeeper();
        src.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 0m),
            new PositionSnapshot("bob", "VALE3", -50, 0m),
            new PositionSnapshot("carol", "ITSA4", 200, 12m), // known basis
        });
        // Mutate the unknown leg before snapshotting.
        src.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 30, 32m); // unknown 70

        var avg = src.RawSnapshotAvgCost();
        var unknown = src.RawSnapshotUnknownBasis();
        var realized = src.RawSnapshotRealized();
        var seen = src.RawSnapshotSeenIds();

        var avgList = avg.Select(a => new PnlAvgCostSnapshot(a.EndClientId, a.Symbol, a.NetQuantity, a.AvgPrice));
        var unknownList = unknown.Select(u => new PnlUnknownBasisSnapshot(u.EndClientId, u.Symbol, u.NetQuantity));
        var realizedDict = realized.ToDictionary(
            r => PnlKeeper.FormatRealizedKey(r.EndClientId, r.Symbol, r.Day),
            r => r.Realized);

        var dst = new PnlKeeper();
        dst.Restore(realizedDict, avgList, seen, unknownList);

        Assert.Equal(70, dst.GetUnknownBasisQty("alice", "PETR4"));
        Assert.Equal(-50, dst.GetUnknownBasisQty("bob", "VALE3"));
        Assert.Null(dst.GetAvgCost("alice", "PETR4"));
        Assert.Null(dst.GetAvgCost("bob", "VALE3"));
        var c = dst.GetAvgCost("carol", "ITSA4")!;
        Assert.Equal(200, c.NetQuantity);
        Assert.Equal(12m, c.AvgPrice);

        // A fill on the restored unknown leg still realises 0 —
        // i.e. the post-restore behaviour matches the live
        // pre-snapshot state.
        Assert.Equal(0m, dst.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 70, 33m));
        Assert.Equal(0, dst.GetUnknownBasisQty("alice", "PETR4"));
        Assert.Null(dst.GetAvgCost("alice", "PETR4"));
    }

    [Fact]
    public void Restore_PrefersUnknownBasis_WhenSnapshotCarriesBothBlocks()
    {
        // Pass-4 (#278) P2#3 — a malformed snapshot containing the
        // same key in BOTH PnlAvgCost and PnlUnknownBasis must not
        // leave a stale avg-cost entry that would resurface after
        // the unknown leg fully closes and silently realise phantom
        // P&L on the next fill. Restore enforces mutual exclusivity
        // by dropping the avg-cost entry (prefer unknown) and
        // bumping the basis_inconsistent metric.
        var dst = new PnlKeeper();
        var avgList = new[] { new PnlAvgCostSnapshot("alice", "PETR4", 100, 25m) };
        var unknownList = new[] { new PnlUnknownBasisSnapshot("alice", "PETR4", 100) };
        dst.Restore(new Dictionary<string, decimal>(), avgList, null, unknownList);

        // Avg-cost for the duplicated key was dropped; unknown wins.
        Assert.Null(dst.GetAvgCost("alice", "PETR4"));
        Assert.Equal(100, dst.GetUnknownBasisQty("alice", "PETR4"));

        // Closing the unknown leg fully realises 0 — and there is
        // no stale avg-cost entry left behind to corrupt subsequent
        // fills.
        Assert.Equal(0m, dst.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Sell, 100, 30m));
        Assert.Equal(0, dst.GetUnknownBasisQty("alice", "PETR4"));
        Assert.Null(dst.GetAvgCost("alice", "PETR4"));

        // A fresh fill after the leg goes flat establishes a real
        // basis at the fill price (not invented from the prior
        // stale avg).
        Assert.Equal(0m, dst.ApplyFillToAvgCost("alice", "PETR4", OrderSide.Buy, 50, 40m));
        var s = dst.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(50, s.NetQuantity);
        Assert.Equal(40m, s.AvgPrice);
    }

    [Fact]
    public void Restore_LeavesDisjointKeysUntouched()
    {
        // Pass-4 (#278) P2#3 — the dedup pass must only affect keys
        // that appear in BOTH dicts; non-overlapping rows are
        // preserved exactly.
        var dst = new PnlKeeper();
        var avgList = new[]
        {
            new PnlAvgCostSnapshot("alice", "PETR4", 100, 25m),
            new PnlAvgCostSnapshot("bob", "VALE3", -50, 60m),
        };
        var unknownList = new[]
        {
            new PnlUnknownBasisSnapshot("carol", "ITSA4", 200),
        };
        dst.Restore(new Dictionary<string, decimal>(), avgList, null, unknownList);

        Assert.Equal(100, dst.GetAvgCost("alice", "PETR4")!.NetQuantity);
        Assert.Equal(-50, dst.GetAvgCost("bob", "VALE3")!.NetQuantity);
        Assert.Equal(200, dst.GetUnknownBasisQty("carol", "ITSA4"));
    }
}
