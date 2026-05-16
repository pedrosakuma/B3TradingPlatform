using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q2.5 (#272). Pure projection that turns a WAL slice into the daily
/// statement DTO consumed by <c>GET /statement/{dayKey}</c> (JSON and
/// CSV). Lives in Application (not in the HTTP layer) so it is trivially
/// unit-testable without spinning the host.
///
/// <para>
/// <b>Day boundary.</b> The day key is a UTC <see cref="DateOnly"/> —
/// matches the canonical boundary already used by
/// <see cref="FeeKeeper"/> and <see cref="PnlKeeper"/>
/// (<c>DateOnly.FromDateTime(ts.UtcDateTime)</c>). The issue mentions a
/// São Paulo session boundary; that mapping is a future projection
/// concern (any TZ conversion happens at the API surface, not here) so
/// every keeper / event in the platform stays in lockstep.
/// </para>
///
/// <para>
/// <b>Positions snapshot.</b> Always derived from the live
/// <see cref="PositionKeeper"/> when <paramref name="useLivePositions"/>
/// is <c>true</c> (today, intraday); past days are projected from the
/// WAL by replaying every fill ER from genesis up to the end of the
/// requested day. This keeps the projection a pure function over the
/// WAL slice the caller passes in.
/// </para>
///
/// <para>
/// <b>IR day-trade pre-calc.</b> Pure informational. For each
/// (endClient, symbol) on the day, we walk the fills in timestamp
/// order, FIFO-pairing buys against sells; for each matched lot the
/// gross profit is <c>(sellPrice - buyPrice) * matchedQty</c>. The
/// taxable amount per symbol is <c>max(grossProfit, 0)</c> — losses on
/// one symbol do NOT offset gains on another (B3 day-trade tax is
/// computed per-symbol per-day under the simplified projection here).
/// The tax rate is 20% (CVM/RFB cash equities day-trade). The whole
/// block is flagged <c>informationalOnly=true, notCollected=true</c>:
/// the platform never withholds anything against the result, it just
/// surfaces it for the trader's own bookkeeping.
/// </para>
/// </summary>
public static class StatementProjection
{
    public const decimal IrDayTradeRate = 0.20m;

    /// <summary>
    /// Build the statement DTO for <paramref name="owner"/> on
    /// <paramref name="dayKey"/> from the WAL events in
    /// <paramref name="walEvents"/> (already filtered to the day, but
    /// re-filtered defensively below). When
    /// <paramref name="livePositions"/> is non-null its
    /// <see cref="PositionKeeper.ForEndClient"/> output is used for the
    /// positions snapshot (today, intraday); otherwise positions are
    /// projected from <paramref name="walEventsAllTime"/> up to the end
    /// of <paramref name="dayKey"/>.
    /// </summary>
    public static DailyStatementDto Build(
        EndClientId owner,
        DateOnly dayKey,
        IReadOnlyList<(long Seq, WalEvent Event)> walEventsAllTime,
        PositionKeeper? livePositions)
    {
        ArgumentNullException.ThrowIfNull(walEventsAllTime);

        var dayStart = new DateTimeOffset(dayKey.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        // ----------------- fills + ownership side-table -----------------
        // ER events carry no owner/symbol; resubstitute them from the
        // matching OrderSubmittedEvent. This mirrors HistoryEndpoints'
        // ownerByClOrdId map but without the cancel/replace fallback
        // complexity: for a daily statement we only care about Fill /
        // PartialFill ERs, which always land on the original ClOrdID
        // and never on a cancel-side or replace-side one.
        var ownerByClOrdId = new Dictionary<ulong, (string Owner, string Symbol, string Side)>();
        var fills = new List<FillRowDto>();
        var feesByType = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal realizedGross = 0m;

        foreach (var (_, evt) in walEventsAllTime)
        {
            switch (evt)
            {
                case OrderSubmittedEvent o:
                    ownerByClOrdId[o.ClOrdId] = (o.EndClientId, o.Symbol, o.Side);
                    break;

                case OrderReplaceRequestedEvent rr:
                    // A replace creates a brand-new ClOrdID that becomes
                    // its own working order; subsequent fill ERs arrive
                    // under it, so register ownership for the new ID
                    // too.
                    ownerByClOrdId[rr.NewClOrdId] = (rr.EndClientId, rr.Symbol, rr.Side);
                    break;

                case ExecutionReportReceivedEvent er:
                    if (er.TimestampUtc < dayStart || er.TimestampUtc >= dayEnd) break;
                    if (!Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind)) break;
                    if (kind is not (ExecKind.Fill or ExecKind.PartialFill)) break;
                    if (!ownerByClOrdId.TryGetValue(er.ClOrdId, out var meta)) break;
                    if (!string.Equals(meta.Owner, owner.Value, StringComparison.Ordinal)) break;
                    fills.Add(new FillRowDto(
                        ExecutionId: $"{er.ClOrdId}:{er.CumulativeQuantity}",
                        ClOrdId: er.ClOrdId.ToString(),
                        OrderId: er.ClOrdId.ToString(),
                        Symbol: meta.Symbol,
                        Side: meta.Side,
                        Quantity: er.LastQuantity,
                        Price: er.LastPrice,
                        TimestampUtc: er.TimestampUtc));
                    break;

                case FeeAccruedEvent fee:
                    if (fee.TimestampUtc < dayStart || fee.TimestampUtc >= dayEnd) break;
                    if (!string.Equals(fee.EndClientId, owner.Value, StringComparison.Ordinal)) break;
                    AddFee(feesByType, "brokerage", fee.Brokerage);
                    AddFee(feesByType, "emolumentos", fee.Emolumentos);
                    AddFee(feesByType, "liquidacao", fee.Liquidacao);
                    break;

                case RealizedPnlEvent pnl:
                    if (pnl.TimestampUtc < dayStart || pnl.TimestampUtc >= dayEnd) break;
                    if (!string.Equals(pnl.EndClientId, owner.Value, StringComparison.Ordinal)) break;
                    if (pnl.DayKey != dayKey) break;
                    realizedGross += pnl.DeltaRealized;
                    break;
            }
        }

        var feeRows = new List<FeeRowDto>(feesByType.Count);
        decimal totalFees = 0m;
        foreach (var kv in feesByType.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            feeRows.Add(new FeeRowDto(kv.Key, kv.Value));
            totalFees += kv.Value;
        }

        // ----------------- positions snapshot -----------------
        IReadOnlyList<PositionRowDto> positions;
        if (livePositions is not null)
        {
            var live = new List<PositionRowDto>();
            foreach (var p in livePositions.ForEndClient(owner))
            {
                if (p.NetQuantity == 0) continue;
                live.Add(new PositionRowDto(p.Symbol, p.NetQuantity, p.AverageEntryPrice));
            }
            live.Sort(static (a, b) => string.CompareOrdinal(a.Symbol, b.Symbol));
            positions = live;
        }
        else
        {
            positions = ProjectPositionsFromWal(owner, dayEnd, walEventsAllTime, ownerByClOrdId);
        }

        // ----------------- IR day-trade pre-calc -----------------
        var ir = ComputeIrDayTrade(fills);

        return new DailyStatementDto(
            DayKey: dayKey.ToString("yyyy-MM-dd"),
            Positions: positions,
            Fills: fills,
            Fees: feeRows,
            FeesTotal: totalFees,
            Pnl: new PnlSummaryDto(realizedGross, totalFees, realizedGross - totalFees),
            IrDayTrade: ir);
    }

    private static void AddFee(Dictionary<string, decimal> bag, string key, decimal value)
    {
        if (value == 0m) return;
        bag[key] = bag.TryGetValue(key, out var prior) ? prior + value : value;
    }

    private static IReadOnlyList<PositionRowDto> ProjectPositionsFromWal(
        EndClientId owner,
        DateTimeOffset dayEnd,
        IReadOnlyList<(long Seq, WalEvent Event)> wal,
        IReadOnlyDictionary<ulong, (string Owner, string Symbol, string Side)> ownerByClOrdId)
    {
        // Replay every fill ER from genesis up to (but not including)
        // dayEnd. Cumulative net qty + avg price is computed via a
        // throwaway PositionKeeper so we share the exact ApplyFill
        // semantics (including the flip-past-zero reset).
        var keeper = new PositionKeeper();
        foreach (var (_, evt) in wal)
        {
            if (evt is not ExecutionReportReceivedEvent er) continue;
            if (er.TimestampUtc >= dayEnd) continue;
            if (!Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind)) continue;
            if (kind is not (ExecKind.Fill or ExecKind.PartialFill)) continue;
            if (!ownerByClOrdId.TryGetValue(er.ClOrdId, out var meta)) continue;
            if (!string.Equals(meta.Owner, owner.Value, StringComparison.Ordinal)) continue;
            if (!Enum.TryParse<OrderSide>(meta.Side, ignoreCase: true, out var side)) continue;
            if (er.LastQuantity <= 0) continue;
            keeper.ApplyFill(owner, meta.Symbol, side, er.LastQuantity, er.LastPrice);
        }

        var rows = new List<PositionRowDto>();
        foreach (var p in keeper.ForEndClient(owner))
        {
            if (p.NetQuantity == 0) continue;
            rows.Add(new PositionRowDto(p.Symbol, p.NetQuantity, p.AverageEntryPrice));
        }
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Symbol, b.Symbol));
        return rows;
    }

    /// <summary>
    /// FIFO pair buys vs sells within the same symbol on the day. For
    /// every matched lot we record <c>(sellPrice - buyPrice) * qty</c>
    /// as gross profit; taxable per symbol is <c>max(gross, 0)</c> and
    /// the tax is 20% of that. Losses do NOT offset gains across
    /// different symbols — kept intentionally simple, the block is
    /// informational only.
    /// </summary>
    private static IrDayTradeDto ComputeIrDayTrade(IReadOnlyList<FillRowDto> fills)
    {
        var perSymbol = new List<IrDayTradeRowDto>();
        decimal totalTax = 0m;

        // Bucket fills by symbol then process in timestamp order.
        var bySymbol = fills
            .GroupBy(f => f.Symbol, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in bySymbol)
        {
            var ordered = group.OrderBy(f => f.TimestampUtc).ToList();
            // Track residual buy and sell lots as FIFO queues of (qty, price).
            var buyLots = new Queue<(long Qty, decimal Price)>();
            var sellLots = new Queue<(long Qty, decimal Price)>();
            long matchedQty = 0;
            decimal grossProfit = 0m;

            foreach (var fill in ordered)
            {
                var isBuy = string.Equals(fill.Side, nameof(OrderSide.Buy), StringComparison.OrdinalIgnoreCase);
                var qty = fill.Quantity;
                var price = fill.Price;

                if (isBuy)
                {
                    // Match against existing sell lots first (the trader
                    // opened a short earlier in the day and is now
                    // closing it).
                    while (qty > 0 && sellLots.Count > 0)
                    {
                        var head = sellLots.Peek();
                        var take = Math.Min(qty, head.Qty);
                        grossProfit += (head.Price - price) * take;
                        matchedQty += take;
                        qty -= take;
                        if (take == head.Qty) sellLots.Dequeue();
                        else { sellLots.Dequeue(); sellLots.Enqueue((head.Qty - take, head.Price)); RotateToFront(sellLots); }
                    }
                    if (qty > 0) buyLots.Enqueue((qty, price));
                }
                else
                {
                    while (qty > 0 && buyLots.Count > 0)
                    {
                        var head = buyLots.Peek();
                        var take = Math.Min(qty, head.Qty);
                        grossProfit += (price - head.Price) * take;
                        matchedQty += take;
                        qty -= take;
                        if (take == head.Qty) buyLots.Dequeue();
                        else { buyLots.Dequeue(); buyLots.Enqueue((head.Qty - take, head.Price)); RotateToFront(buyLots); }
                    }
                    if (qty > 0) sellLots.Enqueue((qty, price));
                }
            }

            if (matchedQty == 0) continue;
            var taxable = grossProfit > 0 ? grossProfit : 0m;
            var tax = decimal.Round(taxable * IrDayTradeRate, 2, MidpointRounding.AwayFromZero);
            perSymbol.Add(new IrDayTradeRowDto(group.Key, matchedQty, grossProfit, taxable, tax));
            totalTax += tax;
        }

        return new IrDayTradeDto(
            InformationalOnly: true,
            NotCollected: true,
            Rate: IrDayTradeRate,
            PerSymbol: perSymbol,
            TotalTax: totalTax);
    }

    // The Queue<T> we use does not support index access, so when we
    // partially consume the head we re-enqueue the remainder. That
    // pushes it to the tail; rotate it back to the front so the FIFO
    // invariant holds (the partially-consumed lot is still the oldest
    // open contra-lot).
    private static void RotateToFront<T>(Queue<T> q)
    {
        // After we dequeued+enqueued the partial residual it sits at
        // the tail. Walk every other element to the back so the
        // partial-residual ends up at the head again.
        var n = q.Count - 1;
        for (var i = 0; i < n; i++) q.Enqueue(q.Dequeue());
    }
}

public sealed record DailyStatementDto(
    string DayKey,
    IReadOnlyList<PositionRowDto> Positions,
    IReadOnlyList<FillRowDto> Fills,
    IReadOnlyList<FeeRowDto> Fees,
    decimal FeesTotal,
    PnlSummaryDto Pnl,
    IrDayTradeDto IrDayTrade);

public sealed record PositionRowDto(string Symbol, long NetQty, decimal AvgPrice);

public sealed record FillRowDto(
    string ExecutionId,
    string ClOrdId,
    string OrderId,
    string Symbol,
    string Side,
    long Quantity,
    decimal Price,
    DateTimeOffset TimestampUtc);

public sealed record FeeRowDto(string FeeType, decimal Total);

public sealed record PnlSummaryDto(decimal RealizedGross, decimal TotalFees, decimal RealizedNet);

public sealed record IrDayTradeDto(
    bool InformationalOnly,
    bool NotCollected,
    decimal Rate,
    IReadOnlyList<IrDayTradeRowDto> PerSymbol,
    decimal TotalTax);

public sealed record IrDayTradeRowDto(
    string Symbol,
    long QtyMatched,
    decimal GrossProfit,
    decimal TaxableProfit,
    decimal TaxAmount);
