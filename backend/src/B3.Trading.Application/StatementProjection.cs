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
/// <b>Positions snapshot.</b> Always derived from a caller-provided
/// immutable snapshot (<see cref="PositionRowDto"/> list) when one is
/// supplied — the API layer captures that under
/// <see cref="Persistence.EventDispatcher.RunExclusive(System.Action)"/>
/// alongside the WAL upper-bound so today's statement cannot tear (a
/// new fill cannot land in <see cref="PositionKeeper"/> after the WAL
/// scan stopped). Past days are projected from the WAL by replaying
/// every fill ER from genesis up to the end of the requested day. This
/// keeps the projection a pure function over the WAL slice the caller
/// passes in.
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
    /// <paramref name="walEventsAllTime"/> (already filtered to the day, but
    /// re-filtered defensively below). <paramref name="firmId"/> scopes
    /// every projection slice (fills, fees, realized PnL, position
    /// replay) to the caller's firm so the same JWT <c>sub</c> active
    /// in multiple firms does not bleed rows across them. When
    /// <paramref name="livePositionsSnapshot"/> is non-null it is used
    /// verbatim as the positions snapshot (already sorted and
    /// zero-quantity-filtered by the caller, and already firm-scoped);
    /// otherwise positions are projected from
    /// <paramref name="walEventsAllTime"/> up to the end of
    /// <paramref name="dayKey"/>. The caller is responsible for
    /// capturing the snapshot atomically with the WAL upper-bound used
    /// to bound <paramref name="walEventsAllTime"/> (today path takes
    /// both under <see cref="Persistence.EventDispatcher.RunExclusive(System.Action)"/>).
    /// </summary>
    public static DailyStatementDto Build(
        EndClientId owner,
        DateOnly dayKey,
        string firmId,
        IReadOnlyList<(long Seq, WalEvent Event)> walEventsAllTime,
        IReadOnlyList<PositionRowDto>? livePositionsSnapshot,
        string? subAccountFilter = null,
        IReadOnlyList<PositionRowDto>? liveMasterSeedFallback = null)
    {
        ArgumentNullException.ThrowIfNull(walEventsAllTime);
        ArgumentException.ThrowIfNullOrEmpty(firmId);

        var normFirm = PositionKeeper.NormalizeFirmId(firmId);
        var dayStart = new DateTimeOffset(dayKey.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        // ----------------- fills + ownership side-table -----------------
        // ER events carry no owner/symbol/firm; resubstitute them from
        // the matching OrderSubmittedEvent (or OrderReplaceRequestedEvent).
        // PR #316 P2: we also track the originating FirmId so all
        // downstream filters (fills, fees, realized PnL, position replay)
        // can scope to the caller's firm — without this the statement
        // for owner X under FIRM01 leaks rows from the same owner login
        // active under FIRM02. Legacy WAL rows without a firm tag are
        // treated as PositionKeeper.DefaultFirmId, matching the
        // back-compat convention used elsewhere. PR #316 P2.2: the
        // metadata also carries the originating SubAccountId so the
        // projection can tag fills/positions per-bucket and so the
        // optional <paramref name="subAccountFilter"/> can be enforced
        // on fees / realized PnL (FeeAccruedEvent has no SubAccountId
        // discriminator beyond the originating submit — same hop the
        // FirmId filter takes).
        var ownerByClOrdId = new Dictionary<ulong, (string Owner, string Symbol, string Side, string FirmId, string? SubAccountId)>();
        var fills = new List<FillRowDto>();
        var feesByType = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal realizedGross = 0m;

        foreach (var (_, evt) in walEventsAllTime)
        {
            switch (evt)
            {
                case OrderSubmittedEvent o:
                    ownerByClOrdId[o.ClOrdId] = (o.EndClientId, o.Symbol, o.Side,
                        PositionKeeper.NormalizeFirmId(o.FirmId), o.SubAccountId);
                    break;

                case OrderReplaceRequestedEvent rr:
                    // A replace creates a brand-new ClOrdID that becomes
                    // its own working order; subsequent fill ERs arrive
                    // under it, so register ownership for the new ID
                    // too. Inherit SubAccountId from the original
                    // submit (OrderReplaceRequestedEvent does not carry
                    // it — Order.HydrateReplacement does the same).
                    var inheritedSub = ownerByClOrdId.TryGetValue(rr.OriginalClOrdId, out var origMeta)
                        ? origMeta.SubAccountId
                        : null;
                    ownerByClOrdId[rr.NewClOrdId] = (rr.EndClientId, rr.Symbol, rr.Side,
                        PositionKeeper.NormalizeFirmId(rr.FirmId), inheritedSub);
                    break;

                case ExecutionReportReceivedEvent er:
                    if (er.TimestampUtc < dayStart || er.TimestampUtc >= dayEnd) break;
                    if (!Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind)) break;
                    if (kind is not (ExecKind.Fill or ExecKind.PartialFill)) break;
                    if (!ownerByClOrdId.TryGetValue(er.ClOrdId, out var meta)) break;
                    if (!string.Equals(meta.Owner, owner.Value, StringComparison.Ordinal)) break;
                    if (!string.Equals(meta.FirmId, normFirm, StringComparison.Ordinal)) break;
                    if (subAccountFilter is not null &&
                        !string.Equals(meta.SubAccountId, subAccountFilter, StringComparison.Ordinal)) break;
                    fills.Add(new FillRowDto(
                        ExecutionId: $"{er.ClOrdId}:{er.CumulativeQuantity}",
                        ClOrdId: er.ClOrdId.ToString(),
                        OrderId: er.ClOrdId.ToString(),
                        Symbol: meta.Symbol,
                        Side: meta.Side,
                        Quantity: er.LastQuantity,
                        Price: er.LastPrice,
                        TimestampUtc: er.TimestampUtc,
                        SubAccountId: meta.SubAccountId));
                    break;

                case FeeAccruedEvent fee:
                    if (fee.TimestampUtc < dayStart || fee.TimestampUtc >= dayEnd) break;
                    if (!string.Equals(fee.EndClientId, owner.Value, StringComparison.Ordinal)) break;
                    // FeeAccruedEvent does not carry FirmId; resolve via
                    // the submit-side ownership map. Legacy fee rows
                    // whose originating Submit pre-dates this map fall
                    // back to DefaultFirmId.
                    if (!ResolveFirm(ownerByClOrdId, fee.ClOrdId, normFirm)) break;
                    // PR #316 P2.2. Sub-account filter is resolved via
                    // the same ownership map (FeeAccruedEvent carries
                    // SubAccountId since #301; fall back to the map for
                    // legacy rows).
                    if (subAccountFilter is not null)
                    {
                        var feeSub = fee.SubAccountId
                            ?? (ownerByClOrdId.TryGetValue(fee.ClOrdId, out var feeMeta) ? feeMeta.SubAccountId : null);
                        if (!string.Equals(feeSub, subAccountFilter, StringComparison.Ordinal)) break;
                    }
                    AddFee(feesByType, "brokerage", fee.Brokerage);
                    AddFee(feesByType, "emolumentos", fee.Emolumentos);
                    AddFee(feesByType, "liquidacao", fee.Liquidacao);
                    break;

                case RealizedPnlEvent pnl:
                    if (pnl.TimestampUtc < dayStart || pnl.TimestampUtc >= dayEnd) break;
                    if (!string.Equals(pnl.EndClientId, owner.Value, StringComparison.Ordinal)) break;
                    if (pnl.DayKey != dayKey) break;
                    // RealizedPnlEvent.FirmId is nullable on legacy WAL
                    // rows; treat null as DefaultFirmId, but prefer the
                    // ownership-map firm when the explicit field is
                    // absent so a complete WAL still routes correctly.
                    var pnlFirm = pnl.FirmId is null
                        ? (ownerByClOrdId.TryGetValue(pnl.ClOrdId, out var pnlMeta)
                            ? pnlMeta.FirmId
                            : PositionKeeper.DefaultFirmId)
                        : PositionKeeper.NormalizeFirmId(pnl.FirmId);
                    if (!string.Equals(pnlFirm, normFirm, StringComparison.Ordinal)) break;
                    if (subAccountFilter is not null)
                    {
                        var pnlSub = pnl.SubAccountId
                            ?? (ownerByClOrdId.TryGetValue(pnl.ClOrdId, out var pnlMeta2) ? pnlMeta2.SubAccountId : null);
                        if (!string.Equals(pnlSub, subAccountFilter, StringComparison.Ordinal)) break;
                    }
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
        if (livePositionsSnapshot is not null)
        {
            positions = subAccountFilter is null
                ? livePositionsSnapshot
                : livePositionsSnapshot
                    .Where(p => string.Equals(p.SubAccountId, subAccountFilter, StringComparison.Ordinal))
                    .ToList();
        }
        else
        {
            positions = ProjectPositionsFromWal(owner, normFirm, dayEnd, walEventsAllTime, ownerByClOrdId, subAccountFilter);

            // PR #316 P2. Today's unfiltered statement falls into this
            // WAL-replay branch the moment any sub-account row exists
            // for (firm, owner) — the live master snapshot would
            // double-count, so we cannot reuse it verbatim. But WAL
            // replay misses any positions seeded directly into
            // PositionKeeper at host startup (TradingHostStartup applies
            // seeds straight to the keeper, never via WAL). To recover
            // them, the caller passes the live master snapshot through
            // <paramref name="liveMasterSeedFallback"/>: for every
            // symbol present there but absent from the WAL-projected
            // master bucket we inject the live row as a master-bucket
            // entry (SubAccountId=null). Symbols already projected from
            // WAL keep their WAL-derived qty/avg untouched (so today's
            // master fills are not overwritten by the seed). Only used
            // for unfiltered queries — a sub-account-scoped statement
            // must never see master-bucket seeds.
            if (liveMasterSeedFallback is not null && subAccountFilter is null && liveMasterSeedFallback.Count > 0)
            {
                var masterPresent = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in positions)
                    if (p.SubAccountId is null) masterPresent.Add(p.Symbol);

                List<PositionRowDto>? merged = null;
                foreach (var seed in liveMasterSeedFallback)
                {
                    if (seed.NetQty == 0) continue;
                    if (masterPresent.Contains(seed.Symbol)) continue;
                    merged ??= new List<PositionRowDto>(positions);
                    merged.Add(new PositionRowDto(seed.Symbol, seed.NetQty, seed.AvgPrice, null));
                }
                if (merged is not null)
                {
                    merged.Sort(static (a, b) =>
                    {
                        var bySym = string.CompareOrdinal(a.Symbol, b.Symbol);
                        if (bySym != 0) return bySym;
                        return string.CompareOrdinal(a.SubAccountId ?? "", b.SubAccountId ?? "");
                    });
                    positions = merged;
                }
            }
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

    private static bool ResolveFirm(
        IReadOnlyDictionary<ulong, (string Owner, string Symbol, string Side, string FirmId, string? SubAccountId)> map,
        ulong clOrdId,
        string targetFirm)
    {
        var firm = map.TryGetValue(clOrdId, out var meta) ? meta.FirmId : PositionKeeper.DefaultFirmId;
        return string.Equals(firm, targetFirm, StringComparison.Ordinal);
    }

    private static IReadOnlyList<PositionRowDto> ProjectPositionsFromWal(
        EndClientId owner,
        string firmId,
        DateTimeOffset dayEnd,
        IReadOnlyList<(long Seq, WalEvent Event)> wal,
        IReadOnlyDictionary<ulong, (string Owner, string Symbol, string Side, string FirmId, string? SubAccountId)> ownerByClOrdId,
        string? subAccountFilter)
    {
        // Replay every fill ER from genesis up to (but not including)
        // dayEnd. Cumulative net qty + avg price is computed via a
        // throwaway PositionKeeper so we share the exact ApplyFill
        // semantics (including the flip-past-zero reset). PR #316 P2:
        // replay is firm-scoped so the historical positions read back
        // through ForEndClientAndFirm match what the live keeper would
        // return for the same (owner, firm) bucket. PR #316 P2.2:
        // bucket by SubAccountId (string?) so each sub-account
        // (including the null "master" bucket) gets its own avg-cost
        // computation; rows are then emitted tagged. When
        // <paramref name="subAccountFilter"/> is set we only project
        // the matching bucket — every other fill is skipped at
        // ingestion so a sub-account A statement never sees
        // sub-account B's lots.
        // Use a sentinel for the null/master bucket so we can use a
        // non-nullable string key.
        const string MasterBucketKey = "\0master";
        var keepers = new Dictionary<string, PositionKeeper>(StringComparer.Ordinal);
        foreach (var (_, evt) in wal)
        {
            if (evt is not ExecutionReportReceivedEvent er) continue;
            if (er.TimestampUtc >= dayEnd) continue;
            if (!Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind)) continue;
            if (kind is not (ExecKind.Fill or ExecKind.PartialFill)) continue;
            if (!ownerByClOrdId.TryGetValue(er.ClOrdId, out var meta)) continue;
            if (!string.Equals(meta.Owner, owner.Value, StringComparison.Ordinal)) continue;
            if (!string.Equals(meta.FirmId, firmId, StringComparison.Ordinal)) continue;
            if (subAccountFilter is not null &&
                !string.Equals(meta.SubAccountId, subAccountFilter, StringComparison.Ordinal)) continue;
            if (!Enum.TryParse<OrderSide>(meta.Side, ignoreCase: true, out var side)) continue;
            if (er.LastQuantity <= 0) continue;
            var bucketKey = meta.SubAccountId ?? MasterBucketKey;
            if (!keepers.TryGetValue(bucketKey, out var keeper))
            {
                keeper = new PositionKeeper();
                keepers[bucketKey] = keeper;
            }
            keeper.ApplyFill(firmId, owner, meta.Symbol, side, er.LastQuantity, er.LastPrice);
        }

        var rows = new List<PositionRowDto>();
        foreach (var bucket in keepers)
        {
            var subTag = bucket.Key == MasterBucketKey ? null : bucket.Key;
            foreach (var p in bucket.Value.ForEndClientAndFirm(firmId, owner))
            {
                if (p.NetQuantity == 0) continue;
                rows.Add(new PositionRowDto(p.Symbol, p.NetQuantity, p.AverageEntryPrice, subTag));
            }
        }
        // Stable order: by symbol, then by sub-account (null bucket
        // first to match the legacy single-row shape).
        rows.Sort(static (a, b) =>
        {
            var bySym = string.CompareOrdinal(a.Symbol, b.Symbol);
            if (bySym != 0) return bySym;
            return string.CompareOrdinal(a.SubAccountId ?? "", b.SubAccountId ?? "");
        });
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

public sealed record PositionRowDto(string Symbol, long NetQty, decimal AvgPrice, string? SubAccountId = null);

public sealed record FillRowDto(
    string ExecutionId,
    string ClOrdId,
    string OrderId,
    string Symbol,
    string Side,
    long Quantity,
    decimal Price,
    DateTimeOffset TimestampUtc,
    string? SubAccountId = null);

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
