using System.Globalization;
using System.Security.Claims;
using System.Text;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q2.5 (#272). Daily statement endpoints — JSON and CSV — projected on
/// demand from the WAL plus the live <see cref="PositionKeeper"/> for
/// the intraday-today path.
///
/// <list type="bullet">
///   <item><c>GET /statement/{dayKey?}</c> — JSON. Default
///   <c>dayKey</c> is today (UTC).</item>
///   <item><c>GET /statement/{dayKey}.csv</c> — same projection as a
///   multi-section UTF-8 BOM CSV (Excel-friendly).</item>
/// </list>
///
/// <para>
/// The economic logic lives in <see cref="StatementProjection"/> so the
/// HTTP layer here is only responsible for: auth resolution, dayKey
/// parsing/validation, draining the WAL writer, and rendering. Both
/// routes share the same projection call so JSON and CSV cannot drift.
/// </para>
/// </summary>
public static class StatementEndpoints
{
    public static IEndpointRouteBuilder MapStatement(this IEndpointRouteBuilder app)
    {
        app.MapGet("/statement/{dayKey?}", [Authorize] async (
            HttpContext ctx,
            EndClientRegistry registry,
            IEventStore store,
            PositionKeeper positions,
            SubAccountPositionKeeper subPositions,
            SubAccountPnlKeeper subAccountPnl,
            SubAccountsRegistry subAccounts,
            EventDispatcher dispatcher,
            string? dayKey,
            string? subAccount,
            CancellationToken ct) =>
        {
            if (!TryResolveDay(dayKey, out var day, out var error))
                return error!;
            var owner = ResolveOwner(ctx, registry);
            var firm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
            if (!TryResolveSubAccount(subAccount, firm, subAccounts, out var subFilter, out var subError))
                return subError!;
            Application.Observability.MetricsRegistry.StatementEndpointRequests.Add(
                1, new KeyValuePair<string, object?>("format", "json"));
            var dto = await BuildAsync(owner, firm, day, store, positions, subPositions, subAccountPnl, dispatcher, subFilter, ct);
            EmitDayTradeMetric(dto);
            return Results.Ok(dto);
        });

        app.MapGet("/statement/{dayKey}.csv", [Authorize] async (
            HttpContext ctx,
            EndClientRegistry registry,
            IEventStore store,
            PositionKeeper positions,
            SubAccountPositionKeeper subPositions,
            SubAccountPnlKeeper subAccountPnl,
            SubAccountsRegistry subAccounts,
            EventDispatcher dispatcher,
            string dayKey,
            string? subAccount,
            CancellationToken ct) =>
        {
            if (!TryResolveDay(dayKey, out var day, out var error))
                return error!;
            var owner = ResolveOwner(ctx, registry);
            var firm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
            if (!TryResolveSubAccount(subAccount, firm, subAccounts, out var subFilter, out var subError))
                return subError!;
            Application.Observability.MetricsRegistry.StatementEndpointRequests.Add(
                1, new KeyValuePair<string, object?>("format", "csv"));
            var dto = await BuildAsync(owner, firm, day, store, positions, subPositions, subAccountPnl, dispatcher, subFilter, ct);
            EmitDayTradeMetric(dto);
            var bytes = RenderCsv(dto);
            return Results.File(
                bytes,
                contentType: "text/csv; charset=utf-8",
                fileDownloadName: $"statement-{dto.DayKey}.csv");
        });

        return app;
    }

    private static async Task<DailyStatementDto> BuildAsync(
        EndClientId owner, string firmId, DateOnly day, IEventStore store, PositionKeeper positions,
        SubAccountPositionKeeper subPositions, SubAccountPnlKeeper subAccountPnl,
        EventDispatcher dispatcher, string? subAccountFilter, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var isToday = day == today;

        // Pass-2 review (#279) P1. Both today and past-day paths capture
        // the WAL upper-bound under EventDispatcher.RunExclusive so a
        // fill ER whose nested Fee/RealizedPnl dispatches are still
        // mid-flight on another thread cannot split across the seq cap:
        // the dispatcher lock guarantees the three appends land before
        // RunExclusive's body observes CurrentSeq. Without it the past-
        // day path could observe an ER without its matching fee/PnL —
        // possible near UTC day rollover when a late ER timestamped
        // "yesterday" is still being dispatched while the reader caps
        // the WAL for yesterday's statement. Today's path additionally
        // snapshots the live PositionKeeper rows under the same lock
        // because the projection consumes them; past-day projection is
        // pure WAL so we skip the keeper snapshot there. The exclusive
        // section stays tiny — no I/O — and the WAL scan + projection
        // run after we exit the lock.
        //
        // PR #316 P2.2 / P1.1. When the caller requested a per-sub-account
        // view (or when any per-sub-account rows exist for this
        // owner+firm), we cannot reuse the master-aggregate live
        // snapshot — it would double-count (master == sum of
        // sub-buckets + null bucket) and its avg-cost is polluted by
        // sub-bucket fills. The projection derives sub-bucket rows
        // from the WAL replay (already firm- and bucket-scoped); we
        // pass a master-bucket-only snapshot built from the bucket
        // store (qty = aggregate − sumSub, avg = bucket basis) so
        // the master row reflects ONLY master-bucket history.
        long capturedSeq = 0;
        IReadOnlyList<PositionRowDto>? capturedPositions = null;
        IReadOnlyList<PositionRowDto>? capturedSeedFallback = null;
        dispatcher.RunExclusive(() =>
        {
            capturedSeq = store.CurrentSeq;
            if (!isToday) return;
            if (subAccountFilter is not null) return;

            var anySub = false;
            // Aggregate (qty, avg) keyed by symbol from the master
            // keeper — used as the avg-cost fallback when the bucket
            // store has no master-bucket basis (pure no-sub-account
            // case, or pre-#316 host with seeds applied without a
            // SubAccountPnlKeeper wire).
            var aggregateBySymbol = new Dictionary<string, (long Qty, decimal Avg)>(StringComparer.Ordinal);
            foreach (var p in positions.ForEndClientAndFirm(firmId, owner))
            {
                if (p.NetQuantity == 0) continue;
                aggregateBySymbol[p.Symbol] = (p.NetQuantity, p.AverageEntryPrice);
            }

            // Sum sub-bucket quantities per symbol. Doubles as the
            // anySub probe — non-empty enumeration means we cannot
            // use the master-aggregate snapshot fast-path.
            var subSumBySymbol = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (_, position) in subPositions.EnumerateForOwner(firmId, owner))
            {
                if (position.NetQuantity == 0) continue;
                anySub = true;
                subSumBySymbol[position.Symbol] = subSumBySymbol.TryGetValue(position.Symbol, out var prior)
                    ? prior + position.NetQuantity
                    : position.NetQuantity;
            }

            if (!anySub)
            {
                var rows = new List<PositionRowDto>(aggregateBySymbol.Count);
                foreach (var kv in aggregateBySymbol)
                    rows.Add(new PositionRowDto(kv.Key, kv.Value.Qty, kv.Value.Avg));
                rows.Sort(static (a, b) => string.CompareOrdinal(a.Symbol, b.Symbol));
                capturedPositions = rows;
                return;
            }

            // PR #316 P1.1. Build the live MASTER-bucket snapshot:
            //   qty = aggregate − sumSub  (invariant tracked by the
            //         bucket store under per-bucket basis)
            //   avg = bucket-store master basis when present (the
            //         authoritative master-only avg cost — set on
            //         seed at host startup and advanced by every
            //         master fill); aggregate avg only as a last
            //         resort for legacy hosts that pre-date the
            //         bucket-basis seed (no sub activity could have
            //         polluted the aggregate avg in that case
            //         because we already know anySub is true ⇒
            //         degraded mode, but the row still has SOME avg
            //         so downstream renderers don't NaN). Symbols
            //         only present in sub-buckets but not in the
            //         aggregate snapshot can't happen (every sub
            //         fill mirrors into the master keeper).
            var symbols = new HashSet<string>(aggregateBySymbol.Keys, StringComparer.Ordinal);
            foreach (var sym in subSumBySymbol.Keys) symbols.Add(sym);
            var masterRows = new List<PositionRowDto>(symbols.Count);
            foreach (var symbol in symbols)
            {
                var aggQty = aggregateBySymbol.TryGetValue(symbol, out var agg) ? agg.Qty : 0;
                var subSum = subSumBySymbol.TryGetValue(symbol, out var s) ? s : 0;
                var masterQty = aggQty - subSum;
                if (masterQty == 0) continue;
                var bucket = subAccountPnl.GetBucketAvgCost(firmId, owner.Value, subAccount: null, symbol);
                var masterAvg = bucket is not null && bucket.NetQuantity == masterQty
                    ? bucket.AvgPrice
                    : agg.Avg;
                masterRows.Add(new PositionRowDto(symbol, masterQty, masterAvg, null));
            }
            masterRows.Sort(static (a, b) => string.CompareOrdinal(a.Symbol, b.Symbol));
            capturedSeedFallback = masterRows;
        });
        var snapshotSeq = capturedSeq;
        var livePositionsSnapshot = capturedPositions;
        var liveMasterSeedFallback = capturedSeedFallback;

        // Drain the writer AFTER taking the dispatcher snapshot so
        // every entry with seq <= snapshotSeq is durable and visible
        // to ReadFromAsync. FlushAsync runs outside the lock — we do
        // not want async I/O while serialising new dispatches.
        await store.FlushAsync(ct);

        var wal = new List<(long Seq, WalEvent Event)>(capacity: 256);
        await foreach (var entry in store.ReadFromAsync(0, ct))
        {
            if (entry.Seq > snapshotSeq) break;
            wal.Add(entry);
        }

        return StatementProjection.Build(owner, day, firmId, wal, livePositionsSnapshot, subAccountFilter, liveMasterSeedFallback);
    }

    private static bool TryResolveSubAccount(
        string? raw, string firmId, SubAccountsRegistry subAccounts,
        out string? subAccountFilter, out IResult? error)
    {
        error = null;
        subAccountFilter = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        SubAccountId saId;
        try { saId = new SubAccountId(raw); }
        catch (ArgumentException ex)
        {
            error = Results.BadRequest(new { error = $"invalid subAccount: {ex.Message}" });
            return false;
        }
        if (!subAccounts.TryGet(firmId, saId.Value, out _))
        {
            error = Results.BadRequest(new { error = $"sub-account '{saId.Value}' is not registered for firm" });
            return false;
        }
        subAccountFilter = saId.Value;
        return true;
    }

    private static void EmitDayTradeMetric(DailyStatementDto dto)
    {
        if (dto.IrDayTrade.PerSymbol.Count > 0)
            Application.Observability.MetricsRegistry.StatementDayTradeDetected.Add(1);
    }

    private static bool TryResolveDay(string? raw, out DateOnly day, out IResult? error)
    {
        var todayUtc = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            day = todayUtc;
            return true;
        }
        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            error = Results.BadRequest(new { error = $"invalid dayKey '{raw}' (expected YYYY-MM-DD)" });
            return false;
        }
        if (day > todayUtc)
        {
            error = Results.NotFound(new { error = $"dayKey '{raw}' is in the future" });
            return false;
        }
        return true;
    }

    private static EndClientId ResolveOwner(HttpContext ctx, EndClientRegistry registry)
    {
        var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
        return registry.Register(sub);
    }

    // -----------------------------------------------------------------
    // CSV rendering
    // -----------------------------------------------------------------

    private static readonly byte[] Utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

    internal static byte[] RenderCsv(DailyStatementDto dto)
    {
        var sb = new StringBuilder(1024);

        WriteSectionHeader(sb, "positions");
        sb.Append("symbol,netQty,avgPrice,subAccount\r\n");
        foreach (var p in dto.Positions)
            sb.Append(Csv(p.Symbol)).Append(',').Append(Num(p.NetQty)).Append(',').Append(Num(p.AvgPrice))
              .Append(',').Append(Csv(p.SubAccountId ?? string.Empty)).Append("\r\n");
        sb.Append("\r\n");

        WriteSectionHeader(sb, "fills");
        sb.Append("executionId,clOrdId,orderId,symbol,side,quantity,price,timestampUtc,subAccount\r\n");
        foreach (var f in dto.Fills)
        {
            sb.Append(Csv(f.ExecutionId)).Append(',')
              .Append(Csv(f.ClOrdId)).Append(',')
              .Append(Csv(f.OrderId)).Append(',')
              .Append(Csv(f.Symbol)).Append(',')
              .Append(Csv(f.Side)).Append(',')
              .Append(Num(f.Quantity)).Append(',')
              .Append(Num(f.Price)).Append(',')
              .Append(f.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(f.SubAccountId ?? string.Empty))
              .Append("\r\n");
        }
        sb.Append("\r\n");

        WriteSectionHeader(sb, "fees");
        sb.Append("feeType,total\r\n");
        foreach (var fee in dto.Fees)
            sb.Append(Csv(fee.FeeType)).Append(',').Append(Num(fee.Total)).Append("\r\n");
        sb.Append(Csv("totalFees")).Append(',').Append(Num(dto.FeesTotal)).Append("\r\n");
        sb.Append("\r\n");

        WriteSectionHeader(sb, "pnl-summary");
        sb.Append("metric,value\r\n");
        sb.Append("realizedGross,").Append(Num(dto.Pnl.RealizedGross)).Append("\r\n");
        sb.Append("totalFees,").Append(Num(dto.Pnl.TotalFees)).Append("\r\n");
        sb.Append("realizedNet,").Append(Num(dto.Pnl.RealizedNet)).Append("\r\n");
        sb.Append("\r\n");

        WriteSectionHeader(sb, "ir-day-trade (informational)");
        sb.Append("symbol,qtyMatched,grossProfit,taxableProfit,taxAmount\r\n");
        foreach (var r in dto.IrDayTrade.PerSymbol)
        {
            sb.Append(Csv(r.Symbol)).Append(',')
              .Append(Num(r.QtyMatched)).Append(',')
              .Append(Num(r.GrossProfit)).Append(',')
              .Append(Num(r.TaxableProfit)).Append(',')
              .Append(Num(r.TaxAmount))
              .Append("\r\n");
        }
        sb.Append("totalTax,,,,").Append(Num(dto.IrDayTrade.TotalTax)).Append("\r\n");

        var payload = Encoding.UTF8.GetBytes(sb.ToString());
        var withBom = new byte[Utf8Bom.Length + payload.Length];
        Buffer.BlockCopy(Utf8Bom, 0, withBom, 0, Utf8Bom.Length);
        Buffer.BlockCopy(payload, 0, withBom, Utf8Bom.Length, payload.Length);
        return withBom;
    }

    private static void WriteSectionHeader(StringBuilder sb, string name) =>
        sb.Append("# ").Append(name).Append("\r\n");

    private static string Num(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// RFC4180-ish field escape: wrap in quotes when the value contains
    /// a comma, quote, CR, or LF; double up internal quotes.
    /// </summary>
    internal static string Csv(string v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        var needsQuote = false;
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (c == ',' || c == '"' || c == '\r' || c == '\n') { needsQuote = true; break; }
        }
        if (!needsQuote) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
