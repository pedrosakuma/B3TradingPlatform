using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q2.5 (#272). Pure-projection coverage for the daily statement DTO.
/// Each test fabricates a minimal WAL slice (submit + ER + fee + pnl
/// events) and asserts on the projection output — no host boot, no
/// HTTP plumbing — so the economic logic (FIFO day-trade pairing,
/// fee aggregation, gross/net P&amp;L) can be exercised in isolation.
/// </summary>
public class StatementProjectionTests
{
    private static readonly EndClientId Alice = new("alice");
    private static readonly EndClientId Bob = new("bob");
    private static readonly DateOnly Day = new(2024, 6, 17);
    private static readonly DateTimeOffset DayStart = new(Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    [Fact]
    public void EmptyDay_ReturnsEmptyStatement_AllTotalsZero()
    {
        var wal = Array.Empty<(long Seq, WalEvent Event)>();

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        Assert.Equal("2024-06-17", dto.DayKey);
        Assert.Empty(dto.Positions);
        Assert.Empty(dto.Fills);
        Assert.Empty(dto.Fees);
        Assert.Equal(0m, dto.FeesTotal);
        Assert.Equal(0m, dto.Pnl.RealizedGross);
        Assert.Equal(0m, dto.Pnl.TotalFees);
        Assert.Equal(0m, dto.Pnl.RealizedNet);
        Assert.True(dto.IrDayTrade.InformationalOnly);
        Assert.True(dto.IrDayTrade.NotCollected);
        Assert.Equal(0.20m, dto.IrDayTrade.Rate);
        Assert.Empty(dto.IrDayTrade.PerSymbol);
        Assert.Equal(0m, dto.IrDayTrade.TotalTax);
    }

    [Fact]
    public void WithFillsFeesAndRealized_TotalsMatchManualSum()
    {
        // alice: 100 PETR4 @ 30 buy, then 50 @ 32 sell.
        // Realized gross = (32 - 30) * 50 = 100.
        // Fees: brokerage 1.20, emolumentos 0.30, liquidacao 0.10 per fill.
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 30m, at: DayStart.AddHours(10))),
            (3, Fee(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m,
                brokerage: 1.20m, emolumentos: 0.30m, liquidacao: 0.10m,
                at: DayStart.AddHours(10))),
            (4, Submit(2UL, Alice, "PETR4", OrderSide.Sell, 50, 32m)),
            (5, Er(2UL, ExecKind.Fill, leaves: 0, cum: 50, last: 50, price: 32m, at: DayStart.AddHours(11))),
            (6, Fee(2UL, Alice, "PETR4", OrderSide.Sell, 50, 32m,
                brokerage: 0.80m, emolumentos: 0.10m, liquidacao: 0.05m,
                at: DayStart.AddHours(11))),
            (7, Realized(2UL, Alice, "PETR4", 100m, at: DayStart.AddHours(11))),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        Assert.Equal(2, dto.Fills.Count);
        Assert.Contains(dto.Fills, f => f.Side == "Buy" && f.Quantity == 100);
        Assert.Contains(dto.Fills, f => f.Side == "Sell" && f.Quantity == 50);

        // Fee aggregation by feeType.
        var feeByType = dto.Fees.ToDictionary(f => f.FeeType, f => f.Total);
        Assert.Equal(2.00m, feeByType["brokerage"]);
        Assert.Equal(0.40m, feeByType["emolumentos"]);
        Assert.Equal(0.15m, feeByType["liquidacao"]);
        Assert.Equal(2.55m, dto.FeesTotal);

        Assert.Equal(100m, dto.Pnl.RealizedGross);
        Assert.Equal(2.55m, dto.Pnl.TotalFees);
        Assert.Equal(97.45m, dto.Pnl.RealizedNet);

        // Position snapshot (projected from WAL): 50 long @ 30.
        var pos = Assert.Single(dto.Positions);
        Assert.Equal("PETR4", pos.Symbol);
        Assert.Equal(50, pos.NetQty);
        Assert.Equal(30m, pos.AvgPrice);
    }

    [Fact]
    public void DayTradeIntradayProfit_AppliesTwentyPercentInformationalTax()
    {
        // alice buys 100 PETR4 @ 30 at 10:00 then sells 100 @ 32 at 11:00.
        // FIFO pair: matched 100, gross = (32-30)*100 = 200, taxable = 200,
        // tax = 200 * 0.20 = 40.00.
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 30m, at: DayStart.AddHours(10))),
            (3, Submit(2UL, Alice, "PETR4", OrderSide.Sell, 100, 32m)),
            (4, Er(2UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 32m, at: DayStart.AddHours(11))),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        var ir = Assert.Single(dto.IrDayTrade.PerSymbol);
        Assert.Equal("PETR4", ir.Symbol);
        Assert.Equal(100, ir.QtyMatched);
        Assert.Equal(200m, ir.GrossProfit);
        Assert.Equal(200m, ir.TaxableProfit);
        Assert.Equal(40.00m, ir.TaxAmount);
        Assert.Equal(40.00m, dto.IrDayTrade.TotalTax);
        Assert.True(dto.IrDayTrade.InformationalOnly);
        Assert.True(dto.IrDayTrade.NotCollected);
    }

    [Fact]
    public void NoDayTradePair_ReturnsZeroIrTax()
    {
        // Only one side of the trade lands today (buy with no offsetting
        // sell). Day-trade detection requires both sides on the SAME day.
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 30m, at: DayStart.AddHours(10))),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        Assert.Empty(dto.IrDayTrade.PerSymbol);
        Assert.Equal(0m, dto.IrDayTrade.TotalTax);
    }

    [Fact]
    public void DayTradeLoss_ProducesZeroTaxOnThatSymbol()
    {
        // alice buys 100 PETR4 @ 30, sells 100 @ 28 → loss of 200.
        // Taxable = max(-200, 0) = 0; tax = 0. The losing symbol still
        // shows up in the per-symbol list (matched qty > 0) so the
        // trader can see the realized day-trade activity, but it adds
        // nothing to totalTax.
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 30m, at: DayStart.AddHours(10))),
            (3, Submit(2UL, Alice, "PETR4", OrderSide.Sell, 100, 28m)),
            (4, Er(2UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 28m, at: DayStart.AddHours(11))),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        var ir = Assert.Single(dto.IrDayTrade.PerSymbol);
        Assert.Equal(-200m, ir.GrossProfit);
        Assert.Equal(0m, ir.TaxableProfit);
        Assert.Equal(0m, ir.TaxAmount);
        Assert.Equal(0m, dto.IrDayTrade.TotalTax);
    }

    [Fact]
    public void DayTradePartialFifo_OnlyMatchesIntradayQuantity()
    {
        // alice buys 100 @ 30 then sells 60 @ 31. FIFO matches 60 lots
        // → gross = (31-30)*60 = 60; tax = 12.00. Residual 40 long
        // remains in positions but is not matched.
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 30m, at: DayStart.AddHours(10))),
            (3, Submit(2UL, Alice, "PETR4", OrderSide.Sell, 60, 31m)),
            (4, Er(2UL, ExecKind.Fill, leaves: 0, cum: 60, last: 60, price: 31m, at: DayStart.AddHours(11))),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        var ir = Assert.Single(dto.IrDayTrade.PerSymbol);
        Assert.Equal(60, ir.QtyMatched);
        Assert.Equal(60m, ir.GrossProfit);
        Assert.Equal(12.00m, ir.TaxAmount);

        var pos = Assert.Single(dto.Positions);
        Assert.Equal(40, pos.NetQty);
    }

    [Fact]
    public void ScopeIsolation_ProjectionFiltersOutOtherEndClients()
    {
        // alice and bob both trade PETR4 today. alice's statement must
        // surface ONLY alice's fills/fees/pnl/positions.
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 30m, at: DayStart.AddHours(10))),
            (3, Fee(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m,
                brokerage: 1m, emolumentos: 0, liquidacao: 0, at: DayStart.AddHours(10))),
            (4, Submit(2UL, Bob, "PETR4", OrderSide.Buy, 50, 30m)),
            (5, Er(2UL, ExecKind.Fill, leaves: 0, cum: 50, last: 50, price: 30m, at: DayStart.AddHours(10))),
            (6, Fee(2UL, Bob, "PETR4", OrderSide.Buy, 50, 30m,
                brokerage: 5m, emolumentos: 0, liquidacao: 0, at: DayStart.AddHours(10))),
            (7, Realized(2UL, Bob, "PETR4", 999m, at: DayStart.AddHours(11))),
        };

        var aliceDto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);
        Assert.Single(aliceDto.Fills);
        Assert.Equal(100, aliceDto.Fills[0].Quantity);
        Assert.Equal(1m, aliceDto.FeesTotal);
        Assert.Equal(0m, aliceDto.Pnl.RealizedGross);
        var pos = Assert.Single(aliceDto.Positions);
        Assert.Equal(100, pos.NetQty);

        var bobDto = StatementProjection.Build(Bob, Day, "TEST", wal, livePositionsSnapshot: null);
        Assert.Single(bobDto.Fills);
        Assert.Equal(50, bobDto.Fills[0].Quantity);
        Assert.Equal(5m, bobDto.FeesTotal);
        Assert.Equal(999m, bobDto.Pnl.RealizedGross);
    }

    [Fact]
    public void EventsOutsideDay_AreExcludedFromTheDayProjection()
    {
        // A fill on Day-1 and a fill on Day+1 must not show up on Day's
        // statement; only the in-window fill is surfaced.
        var prev = DayStart.AddDays(-1).AddHours(11);
        var next = DayStart.AddDays(1).AddHours(11);
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 10, 30m)),
            (2, Er(1UL, ExecKind.Fill, 0, 10, 10, 30m, prev)),
            (3, Submit(2UL, Alice, "PETR4", OrderSide.Buy, 20, 31m)),
            (4, Er(2UL, ExecKind.Fill, 0, 20, 20, 31m, DayStart.AddHours(10))),
            (5, Submit(3UL, Alice, "PETR4", OrderSide.Buy, 30, 32m)),
            (6, Er(3UL, ExecKind.Fill, 0, 30, 30, 32m, next)),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);
        var fill = Assert.Single(dto.Fills);
        Assert.Equal(20, fill.Quantity);
        Assert.Equal(31m, fill.Price);

        // Positions are end-of-day (Day): includes the Day-1 fill (10)
        // and Day's fill (20), excludes the Day+1 fill.
        var pos = Assert.Single(dto.Positions);
        Assert.Equal(30, pos.NetQty);
    }

    [Fact]
    public void DayTradeMixedProfitAndLoss_NetsWithinSymbol()
    {
        // Pass-2 review (#279) P2 regression. Within the SAME end-client
        // and SAME symbol on the SAME day FIFO can pair lots that
        // produce both profits and losses; the projection must net
        // them (per-symbol) before applying the 20% rate:
        //   buy  100 @ 30  →  buy lot1 (100 @ 30)
        //   buy  100 @ 35  →  buy lot2 (100 @ 35)
        //   sell 100 @ 40  →  pairs FIFO against lot1 → +(40-30)*100 = +1000
        //   sell 100 @ 31  →  pairs FIFO against lot2 → +(31-35)*100 =  -400
        //   net per-symbol gross =  600  →  taxable 600 → tax 120.00
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, 0, 100, 100, 30m, DayStart.AddHours(10))),
            (3, Submit(2UL, Alice, "PETR4", OrderSide.Buy, 100, 35m)),
            (4, Er(2UL, ExecKind.Fill, 0, 100, 100, 35m, DayStart.AddHours(11))),
            (5, Submit(3UL, Alice, "PETR4", OrderSide.Sell, 100, 40m)),
            (6, Er(3UL, ExecKind.Fill, 0, 100, 100, 40m, DayStart.AddHours(12))),
            (7, Submit(4UL, Alice, "PETR4", OrderSide.Sell, 100, 31m)),
            (8, Er(4UL, ExecKind.Fill, 0, 100, 100, 31m, DayStart.AddHours(13))),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        var ir = Assert.Single(dto.IrDayTrade.PerSymbol);
        Assert.Equal("PETR4", ir.Symbol);
        Assert.Equal(200, ir.QtyMatched);
        Assert.Equal(600m, ir.GrossProfit);
        Assert.Equal(600m, ir.TaxableProfit);
        Assert.Equal(120.00m, ir.TaxAmount);
        Assert.Equal(120.00m, dto.IrDayTrade.TotalTax);
    }

    [Fact]
    public void DayTradeMixedProfitAndLoss_NetLossYieldsZeroTaxWithNoCredit()
    {
        // Same shape as the profit case but the loss leg dominates:
        //   buy  100 @ 30, buy 100 @ 35
        //   sell 100 @ 32  → +(32-30)*100 = +200
        //   sell 100 @ 25  → +(25-35)*100 = -1000
        //   net gross = -800 → taxable = 0 → tax = 0 (no credit carried).
        var wal = new List<(long Seq, WalEvent Event)>
        {
            (1, Submit(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, 0, 100, 100, 30m, DayStart.AddHours(10))),
            (3, Submit(2UL, Alice, "PETR4", OrderSide.Buy, 100, 35m)),
            (4, Er(2UL, ExecKind.Fill, 0, 100, 100, 35m, DayStart.AddHours(11))),
            (5, Submit(3UL, Alice, "PETR4", OrderSide.Sell, 100, 32m)),
            (6, Er(3UL, ExecKind.Fill, 0, 100, 100, 32m, DayStart.AddHours(12))),
            (7, Submit(4UL, Alice, "PETR4", OrderSide.Sell, 100, 25m)),
            (8, Er(4UL, ExecKind.Fill, 0, 100, 100, 25m, DayStart.AddHours(13))),
        };

        var dto = StatementProjection.Build(Alice, Day, "TEST", wal, livePositionsSnapshot: null);

        var ir = Assert.Single(dto.IrDayTrade.PerSymbol);
        Assert.Equal(-800m, ir.GrossProfit);
        Assert.Equal(0m, ir.TaxableProfit);
        Assert.Equal(0m, ir.TaxAmount);
        Assert.Equal(0m, dto.IrDayTrade.TotalTax);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static OrderSubmittedEvent Submit(ulong clOrdId, EndClientId owner, string symbol, OrderSide side, long qty, decimal price) =>
        new()
        {
            ClOrdId = clOrdId,
            EndClientId = owner.Value,
            FirmId = "TEST",
            Symbol = symbol,
            SecurityId = 4321UL,
            Side = side.ToString(),
            Type = "Limit",
            Quantity = qty,
            Price = price,
            TimestampUtc = DayStart.AddHours(9),
        };

    private static ExecutionReportReceivedEvent Er(
        ulong clOrdId, ExecKind kind, long leaves, long cum, long last, decimal price, DateTimeOffset at) =>
        new()
        {
            ClOrdId = clOrdId,
            ExecKind = kind.ToString(),
            LeavesQuantity = leaves,
            CumulativeQuantity = cum,
            LastQuantity = last,
            LastPrice = price,
            Synthetic = false,
            TimestampUtc = at,
        };

    private static FeeAccruedEvent Fee(
        ulong clOrdId, EndClientId owner, string symbol, OrderSide side, long qty, decimal price,
        decimal brokerage, decimal emolumentos, decimal liquidacao, DateTimeOffset at)
    {
        var notional = qty * price;
        return new FeeAccruedEvent
        {
            ClOrdId = clOrdId,
            ExecutionId = $"{clOrdId}:{qty}",
            EndClientId = owner.Value,
            Symbol = symbol,
            Side = side.ToString(),
            FillQuantity = qty,
            FillPrice = price,
            Notional = notional,
            Brokerage = brokerage,
            Emolumentos = emolumentos,
            Liquidacao = liquidacao,
            Total = brokerage + emolumentos + liquidacao,
            TimestampUtc = at,
        };
    }

    private static RealizedPnlEvent Realized(
        ulong clOrdId, EndClientId owner, string symbol, decimal delta, DateTimeOffset at) =>
        new()
        {
            ClOrdId = clOrdId,
            ExecutionId = $"{clOrdId}:r",
            EndClientId = owner.Value,
            Symbol = symbol,
            DayKey = DateOnly.FromDateTime(at.UtcDateTime),
            DeltaRealized = delta,
            RunningTotal = delta,
            TimestampUtc = at,
        };

    [Fact]
    public void ScopeIsolation_ProjectionFiltersOutOtherFirms_SameOwner()
    {
        // PR #316 P2.1. Same JWT sub trades in FIRM01 and FIRM02 on
        // day D. A statement requested as FIRM01 must include only the
        // FIRM01 fills, fees, realized PnL and projected positions —
        // no leakage from the FIRM02 slice of the same owner login.
        var wal = new List<(long Seq, WalEvent Event)>
        {
            // FIRM01 leg: buy 100 PETR4 @ 30.
            (1, SubmitWithFirm(1UL, Alice, "FIRM01", "PETR4", OrderSide.Buy, 100, 30m)),
            (2, Er(1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, price: 30m, at: DayStart.AddHours(10))),
            (3, Fee(1UL, Alice, "PETR4", OrderSide.Buy, 100, 30m,
                brokerage: 1m, emolumentos: 0, liquidacao: 0, at: DayStart.AddHours(10))),
            (4, RealizedWithFirm(1UL, Alice, "FIRM01", "PETR4", 111m, at: DayStart.AddHours(10))),

            // FIRM02 leg: same owner, different firm — must not leak.
            (5, SubmitWithFirm(2UL, Alice, "FIRM02", "VALE3", OrderSide.Buy, 50, 60m)),
            (6, Er(2UL, ExecKind.Fill, leaves: 0, cum: 50, last: 50, price: 60m, at: DayStart.AddHours(11))),
            (7, Fee(2UL, Alice, "VALE3", OrderSide.Buy, 50, 60m,
                brokerage: 9m, emolumentos: 0, liquidacao: 0, at: DayStart.AddHours(11))),
            (8, RealizedWithFirm(2UL, Alice, "FIRM02", "VALE3", 999m, at: DayStart.AddHours(11))),
        };

        var firm01 = StatementProjection.Build(Alice, Day, "FIRM01", wal, livePositionsSnapshot: null);
        var fill01 = Assert.Single(firm01.Fills);
        Assert.Equal("PETR4", fill01.Symbol);
        Assert.Equal(100, fill01.Quantity);
        Assert.Equal(1m, firm01.FeesTotal);
        Assert.Equal(111m, firm01.Pnl.RealizedGross);
        var pos01 = Assert.Single(firm01.Positions);
        Assert.Equal("PETR4", pos01.Symbol);
        Assert.Equal(100, pos01.NetQty);

        // Sanity: the FIRM02 view sees only its own slice.
        var firm02 = StatementProjection.Build(Alice, Day, "FIRM02", wal, livePositionsSnapshot: null);
        var fill02 = Assert.Single(firm02.Fills);
        Assert.Equal("VALE3", fill02.Symbol);
        Assert.Equal(9m, firm02.FeesTotal);
        Assert.Equal(999m, firm02.Pnl.RealizedGross);
    }

    private static OrderSubmittedEvent SubmitWithFirm(
        ulong clOrdId, EndClientId owner, string firmId, string symbol, OrderSide side, long qty, decimal price) =>
        new()
        {
            ClOrdId = clOrdId,
            EndClientId = owner.Value,
            FirmId = firmId,
            Symbol = symbol,
            SecurityId = 4321UL,
            Side = side.ToString(),
            Type = "Limit",
            Quantity = qty,
            Price = price,
            TimestampUtc = DayStart.AddHours(9),
        };

    private static RealizedPnlEvent RealizedWithFirm(
        ulong clOrdId, EndClientId owner, string firmId, string symbol, decimal delta, DateTimeOffset at) =>
        new()
        {
            ClOrdId = clOrdId,
            ExecutionId = $"{clOrdId}:r",
            EndClientId = owner.Value,
            FirmId = firmId,
            Symbol = symbol,
            DayKey = DateOnly.FromDateTime(at.UtcDateTime),
            DeltaRealized = delta,
            RunningTotal = delta,
            TimestampUtc = at,
        };
}
