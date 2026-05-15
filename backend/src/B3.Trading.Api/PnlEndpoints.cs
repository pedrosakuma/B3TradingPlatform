using System.Security.Claims;
using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q2.4 (#271). GET /pnl/today — projects the authenticated end-client's
/// realized + unrealized P&amp;L for the current UTC day.
///
/// <para>
/// Realized comes straight from <see cref="PnlKeeper"/> (durable: WAL
/// projection + snapshot). Unrealized is derived on the fly from
/// <see cref="PositionKeeper"/> + <see cref="IReferencePrice"/> — never
/// persisted, since the inputs (live position + ref price) move every
/// tick. Symbols whose ref-price lookup misses are omitted from the
/// unrealized list (rather than reported as 0) to keep the totals
/// honest; the symbol still appears in the realized list when it has
/// realized activity for the day.
/// </para>
///
/// <para>
/// Auth: same shape as /positions and /balance — JWT <c>sub</c> is the
/// end-client identity, query parameters are ignored. The spec mentions
/// <c>?endClient=…</c> but cross-account reads are not allowed in v1.
/// </para>
/// </summary>
public static class PnlEndpoints
{
    public static IEndpointRouteBuilder MapPnl(this IEndpointRouteBuilder app)
    {
        app.MapGet("/pnl/today", [Authorize] (
            HttpContext ctx,
            EndClientRegistry registry,
            PnlKeeper pnl,
            PositionKeeper positions,
            IReferencePrice refPrice) =>
        {
            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
            var owner = registry.Register(sub);
            Application.Observability.MetricsRegistry.PnlEndpointRequests.Add(1);
            var snap = PnlProjection.Build(owner, pnl, positions, refPrice);
            return Results.Ok(snap);
        });

        return app;
    }
}

public sealed record PnlSymbolDto(
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice,
    decimal? ReferencePrice,
    decimal Realized,
    decimal? Unrealized);

public sealed record PnlTodayDto(
    string Day,
    decimal RealizedTotal,
    decimal UnrealizedTotal,
    IReadOnlyList<PnlSymbolDto> Symbols);

/// <summary>
/// Shared composer for the REST endpoint and the WebSocket
/// <c>pnl.me</c> snapshot/delta — both must surface identical numbers
/// for the same input state. Implemented as a static helper rather than
/// a class so it stays pure: no DI, no ambient state, easy to test in
/// isolation.
/// </summary>
public static class PnlProjection
{
    public static PnlTodayDto Build(
        EndClientId owner,
        PnlKeeper pnl,
        PositionKeeper positions,
        IReferencePrice refPrice,
        DateOnly? day = null)
    {
        var d = day ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var symbols = new Dictionary<string, (long Qty, decimal Avg, decimal? Ref, decimal Realized, decimal? Unrealized)>(StringComparer.Ordinal);

        // Seed from open positions — these always carry an avg-cost basis
        // and a (possibly stale) ref-price lookup result.
        foreach (var p in positions.ForEndClient(owner))
        {
            decimal? rp = null;
            decimal? un = null;
            if (refPrice.TryGet(p.Symbol, out var px))
            {
                rp = px;
                un = p.NetQuantity >= 0
                    ? (px - p.AverageEntryPrice) * p.NetQuantity
                    : (p.AverageEntryPrice - px) * (-p.NetQuantity);
            }
            symbols[p.Symbol] = (p.NetQuantity, p.AverageEntryPrice, rp, 0m, un);
        }

        // Layer in realized for the day. A symbol may have realized
        // activity (closed-out earlier) without a current open position
        // — those rows still appear, with NetQuantity=0 / Unrealized=null.
        foreach (var (symbol, realized) in pnl.ForEndClientDay(owner.Value, d))
        {
            if (symbols.TryGetValue(symbol, out var existing))
            {
                symbols[symbol] = (existing.Qty, existing.Avg, existing.Ref, realized, existing.Unrealized);
            }
            else
            {
                symbols[symbol] = (0, 0m, null, realized, null);
            }
        }

        var rows = new List<PnlSymbolDto>(symbols.Count);
        decimal realizedTotal = 0m;
        decimal unrealizedTotal = 0m;
        foreach (var kv in symbols)
        {
            var v = kv.Value;
            rows.Add(new PnlSymbolDto(kv.Key, v.Qty, v.Avg, v.Ref, v.Realized, v.Unrealized));
            realizedTotal += v.Realized;
            if (v.Unrealized is not null) unrealizedTotal += v.Unrealized.Value;
        }
        return new PnlTodayDto(d.ToString("yyyy-MM-dd"), realizedTotal, unrealizedTotal, rows);
    }
}
