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
            EventDispatcher dispatcher,
            string? dayKey,
            CancellationToken ct) =>
        {
            if (!TryResolveDay(dayKey, out var day, out var error))
                return error!;
            var owner = ResolveOwner(ctx, registry);
            var firm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
            Application.Observability.MetricsRegistry.StatementEndpointRequests.Add(
                1, new KeyValuePair<string, object?>("format", "json"));
            var dto = await BuildAsync(owner, firm, day, store, positions, dispatcher, ct);
            EmitDayTradeMetric(dto);
            return Results.Ok(dto);
        });

        app.MapGet("/statement/{dayKey}.csv", [Authorize] async (
            HttpContext ctx,
            EndClientRegistry registry,
            IEventStore store,
            PositionKeeper positions,
            EventDispatcher dispatcher,
            string dayKey,
            CancellationToken ct) =>
        {
            if (!TryResolveDay(dayKey, out var day, out var error))
                return error!;
            var owner = ResolveOwner(ctx, registry);
            var firm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
            Application.Observability.MetricsRegistry.StatementEndpointRequests.Add(
                1, new KeyValuePair<string, object?>("format", "csv"));
            var dto = await BuildAsync(owner, firm, day, store, positions, dispatcher, ct);
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
        EventDispatcher dispatcher, CancellationToken ct)
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
        long capturedSeq = 0;
        IReadOnlyList<PositionRowDto>? capturedPositions = null;
        dispatcher.RunExclusive(() =>
        {
            capturedSeq = store.CurrentSeq;
            if (!isToday) return;
            var rows = new List<PositionRowDto>();
            foreach (var p in positions.ForEndClientAndFirm(firmId, owner))
            {
                if (p.NetQuantity == 0) continue;
                rows.Add(new PositionRowDto(p.Symbol, p.NetQuantity, p.AverageEntryPrice));
            }
            rows.Sort(static (a, b) => string.CompareOrdinal(a.Symbol, b.Symbol));
            capturedPositions = rows;
        });
        var snapshotSeq = capturedSeq;
        var livePositionsSnapshot = capturedPositions;

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

        return StatementProjection.Build(owner, day, firmId, wal, livePositionsSnapshot);
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
        sb.Append("symbol,netQty,avgPrice\r\n");
        foreach (var p in dto.Positions)
            sb.Append(Csv(p.Symbol)).Append(',').Append(Num(p.NetQty)).Append(',').Append(Num(p.AvgPrice)).Append("\r\n");
        sb.Append("\r\n");

        WriteSectionHeader(sb, "fills");
        sb.Append("executionId,clOrdId,orderId,symbol,side,quantity,price,timestampUtc\r\n");
        foreach (var f in dto.Fills)
        {
            sb.Append(Csv(f.ExecutionId)).Append(',')
              .Append(Csv(f.ClOrdId)).Append(',')
              .Append(Csv(f.OrderId)).Append(',')
              .Append(Csv(f.Symbol)).Append(',')
              .Append(Csv(f.Side)).Append(',')
              .Append(Num(f.Quantity)).Append(',')
              .Append(Num(f.Price)).Append(',')
              .Append(f.TimestampUtc.ToString("O", CultureInfo.InvariantCulture))
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
