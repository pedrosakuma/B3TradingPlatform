using B3.Trading.Application;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q2.3 (#270). Unit-level coverage for <see cref="FeeKeeper"/>.
/// End-to-end snapshot+replay coverage lives in
/// <c>FeeKeeperRecoveryTests</c>; ER integration in
/// <c>ExecutionReportProcessorFeeTests</c>.
/// </summary>
public class FeeKeeperTests
{
    private static FeeAccruedEvent Evt(string clOrdId, ulong cum, string ec, decimal total, DateTimeOffset ts)
        => new()
        {
            ClOrdId = ulong.Parse(clOrdId),
            ExecutionId = clOrdId + ":" + cum,
            EndClientId = ec,
            Symbol = "PETR4",
            Side = "Buy",
            FillQuantity = 10,
            FillPrice = 30m,
            Notional = 300m,
            Brokerage = total - 1m,
            Emolumentos = 0.5m,
            Liquidacao = 0.5m,
            Total = total,
            TimestampUtc = ts,
        };

    [Fact]
    public void Apply_AdvancesDayTotal()
    {
        var keeper = new FeeKeeper();
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.True(keeper.Apply(Evt("1", 10, "alice", 5m, t)));
        Assert.True(keeper.Apply(Evt("2", 10, "alice", 7m, t)));
        Assert.Equal(12m, keeper.GetDayTotal("alice", new DateOnly(2025, 1, 15)));
    }

    [Fact]
    public void Apply_DifferentDays_BucketsSeparately()
    {
        var keeper = new FeeKeeper();
        var d1 = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2025, 1, 16, 12, 0, 0, TimeSpan.Zero);
        keeper.Apply(Evt("1", 10, "alice", 5m, d1));
        keeper.Apply(Evt("2", 10, "alice", 7m, d2));
        Assert.Equal(5m, keeper.GetDayTotal("alice", new DateOnly(2025, 1, 15)));
        Assert.Equal(7m, keeper.GetDayTotal("alice", new DateOnly(2025, 1, 16)));
    }

    [Fact]
    public void Apply_DuplicateExecutionId_Idempotent()
    {
        var keeper = new FeeKeeper();
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var evt = Evt("1", 10, "alice", 5m, t);
        Assert.True(keeper.Apply(evt));
        Assert.False(keeper.Apply(evt));
        Assert.Equal(5m, keeper.GetDayTotal("alice", new DateOnly(2025, 1, 15)));
    }

    [Fact]
    public void GetDayTotal_UnknownKey_ReturnsZero()
    {
        Assert.Equal(0m, new FeeKeeper().GetDayTotal("ghost", new DateOnly(2025, 1, 1)));
    }

    [Fact]
    public void Snapshot_Restore_RoundTrips_TotalsAndSeenSet()
    {
        var src = new FeeKeeper();
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        src.Apply(Evt("1", 10, "alice", 5m, t));
        src.Apply(Evt("2", 20, "alice", 7m, t));
        src.Apply(Evt("3", 10, "bob", 11m, t));

        var raw = src.RawSnapshot();
        var seen = src.RawSnapshotSeenIds();
        var dict = raw.ToDictionary(r => FeeKeeper.FormatKey(r.EndClientId, r.Day), r => r.Total);

        var dst = new FeeKeeper();
        dst.Restore(dict, seen);

        Assert.Equal(12m, dst.GetDayTotal("alice", new DateOnly(2025, 1, 15)));
        Assert.Equal(11m, dst.GetDayTotal("bob", new DateOnly(2025, 1, 15)));
        // Re-applying any seen event is a no-op after restore.
        Assert.False(dst.Apply(Evt("1", 10, "alice", 5m, t)));
        Assert.Equal(12m, dst.GetDayTotal("alice", new DateOnly(2025, 1, 15)));
    }

    [Fact]
    public void RawSnapshot_SkipsZeroBalances()
    {
        // Zero rows can't materialise via Apply (Total>0 always); we
        // exercise the skip via direct Restore of a zero entry.
        var keeper = new FeeKeeper();
        keeper.Restore(new Dictionary<string, decimal>
        {
            [FeeKeeper.FormatKey("alice", new DateOnly(2025, 1, 15))] = 0m,
            [FeeKeeper.FormatKey("bob", new DateOnly(2025, 1, 15))] = 11m,
        });
        var raw = keeper.RawSnapshot();
        Assert.Single(raw);
        Assert.Equal("bob", raw[0].EndClientId);
    }

    [Fact]
    public void FormatKey_ParsesBack()
    {
        var key = FeeKeeper.FormatKey("alice", new DateOnly(2025, 1, 15));
        Assert.True(FeeKeeper.TryParseKey(key, out var ec, out var d));
        Assert.Equal("alice", ec);
        Assert.Equal(new DateOnly(2025, 1, 15), d);
    }

    // ----- #387: cash debit wired through FeeKeeper.Apply -----

    [Fact]
    public void Apply_WithCashLedger_DebitsAvailable_OnFirstSeen()
    {
        var cash = new CashLedger();
        cash.SeedIfAbsent(new B3.Trading.Domain.EndClientId("alice"), 1_000m);
        var keeper = new FeeKeeper(cash);
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.True(keeper.Apply(Evt("1", 10, "alice", 0.50m, t)));

        Assert.Equal(999.50m, cash.GetAvailable(new B3.Trading.Domain.EndClientId("alice")));
    }

    [Fact]
    public void Apply_WithCashLedger_DedupesByExecutionId_NoDoubleDebit()
    {
        var cash = new CashLedger();
        cash.SeedIfAbsent(new B3.Trading.Domain.EndClientId("alice"), 1_000m);
        var keeper = new FeeKeeper(cash);
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var evt = Evt("1", 10, "alice", 0.50m, t);

        Assert.True(keeper.Apply(evt));
        Assert.False(keeper.Apply(evt));   // replay / retransmit

        Assert.Equal(999.50m, cash.GetAvailable(new B3.Trading.Domain.EndClientId("alice")));
    }

    [Fact]
    public void Apply_WithoutCashLedger_AdvancesTotalsButDebitsNothing()
    {
        // Statement-only / test contexts construct FeeKeeper without
        // a CashLedger — totals must still advance.
        var keeper = new FeeKeeper(cash: null);
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.True(keeper.Apply(Evt("1", 10, "alice", 5m, t)));

        Assert.Equal(5m, keeper.GetDayTotal("alice", new DateOnly(2025, 1, 15)));
    }

    // #387. WAL replay (snapshot-before-fee path): Restore loads
    // seen-set + totals; subsequent FeeAccruedEvent replay must be a
    // no-op for BOTH totals and cash.
    [Fact]
    public void Apply_AfterRestoreWithSeenId_NoDoubleDebit()
    {
        var cash = new CashLedger();
        cash.SeedIfAbsent(new B3.Trading.Domain.EndClientId("alice"), 1_000m);
        // Pretend a previous run already debited the fee → snapshot
        // captured Available=999.50 and seen-set={"1:10"}.
        cash.ApplyFee(new B3.Trading.Domain.EndClientId("alice"), 0.50m);
        var keeper = new FeeKeeper(cash);
        keeper.Restore(new Dictionary<string, decimal>(), new[] { "1:10" });

        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.False(keeper.Apply(Evt("1", 10, "alice", 0.50m, t)));

        Assert.Equal(999.50m, cash.GetAvailable(new B3.Trading.Domain.EndClientId("alice")));
    }

    // #387. FinalizeReplay path: ER-then-crash window. No durable fee
    // event came through but the ER did. Materialise + debit cash.
    [Fact]
    public void FinalizeReplay_MaterialisesFee_AndDebitsCash()
    {
        var cash = new CashLedger();
        cash.SeedIfAbsent(new B3.Trading.Domain.EndClientId("alice"), 1_000m);
        var keeper = new FeeKeeper(cash);
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        keeper.RegisterPendingReplaySynth("1:10", "alice", "PETR4",
            B3.Trading.Domain.OrderSide.Buy, 10, 30m, t);

        var calc = new StaticFeeCalculator(0.75m);
        var n = keeper.FinalizeReplay(calc);

        Assert.Equal(1, n);
        Assert.Equal(0.75m, keeper.GetDayTotal("alice", new DateOnly(2025, 1, 15)));
        Assert.Equal(999.25m, cash.GetAvailable(new B3.Trading.Domain.EndClientId("alice")));
    }

    private sealed class StaticFeeCalculator : IFeeCalculator
    {
        private readonly decimal _total;
        public StaticFeeCalculator(decimal total) { _total = total; }
        public FeeBreakdown Compute(string symbol, B3.Trading.Domain.OrderSide side, long quantity, decimal price)
            => new(_total, 0m, 0m, _total);
    }
}
