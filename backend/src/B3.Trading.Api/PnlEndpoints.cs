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
/// Pass-1 review (#278) P2#4. Response shape matches the issue
/// contract: parallel <c>realized</c> / <c>unrealized</c> arrays plus
/// <c>totalRealized</c> / <c>totalUnrealized</c>. The WS
/// <c>pnl.me</c> channel publishes the SAME projection (snapshot and
/// delta), shared via <see cref="PnlProjection.Build"/>.
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
            SubAccountPnlKeeper subPnl,
            SubAccountsRegistry subAccounts,
            IReferencePrice refPrice,
            string? subAccount) =>
        {
            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
            var owner = registry.Register(sub);
            Application.Observability.MetricsRegistry.PnlEndpointRequests.Add(1);
            // Q4.1 (#301). With ?subAccount=X the response is restricted
            // to the per-sub-account realized totals; the unrealized
            // side is intentionally empty because per-sub-account
            // avg-cost basis is not tracked in this slice (see PR
            // body — deferred to a follow-up). Without the filter the
            // legacy master-aggregate projection is returned.
            if (!string.IsNullOrWhiteSpace(subAccount))
            {
                SubAccountId saId;
                try { saId = new SubAccountId(subAccount); }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = $"invalid subAccount: {ex.Message}" });
                }
                var firm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
                if (!subAccounts.TryGet(firm, saId.Value, out _))
                    return Results.BadRequest(new { error = $"sub-account '{saId.Value}' is not registered for firm" });
                var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
                var realized = new List<PnlRealizedEntry>();
                decimal total = 0m;
                foreach (var (symbol, value) in subPnl.ForSubAccountDay(firm, owner.Value, saId, day))
                {
                    if (value == 0m) continue;
                    realized.Add(new PnlRealizedEntry(symbol, value));
                    total += value;
                }
                return Results.Ok(new PnlTodayDto(realized,
                    Array.Empty<PnlUnrealizedEntry>(), total, 0m));
            }
            var legacyFirm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
            var snap = PnlProjection.Build(owner, legacyFirm, pnl, positions, refPrice);
            return Results.Ok(snap);
        });

        return app;
    }
}

public sealed record PnlRealizedEntry(string Symbol, decimal Value);

public sealed record PnlUnrealizedEntry(
    string Symbol,
    decimal Value,
    decimal RefPrice,
    long Position,
    decimal AvgPrice);

/// <summary>
/// Pass-1 review (#278) P2#4. Shape mandated by the Q2.4 issue
/// contract: parallel <c>realized</c> / <c>unrealized</c> arrays plus
/// the two totals. The unrealized array carries the inputs
/// (<c>refPrice</c>, <c>position</c>, <c>avgPrice</c>) so a client
/// can recompute the value locally; only symbols with a live
/// reference price appear in <c>unrealized</c>, while <c>realized</c>
/// surfaces every symbol with non-zero realized P&amp;L for the day
/// (regardless of whether it still has an open position).
/// </summary>
public sealed record PnlTodayDto(
    IReadOnlyList<PnlRealizedEntry> Realized,
    IReadOnlyList<PnlUnrealizedEntry> Unrealized,
    decimal TotalRealized,
    decimal TotalUnrealized);

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
        DateOnly? day = null) =>
        Build(owner, PnlKeeper.DefaultFirmId, pnl, positions, refPrice, day);

    /// <summary>
    /// PR #316 P1. Firm-scoped overload. The same JWT <c>sub</c>
    /// registered in two firms must see only positions, basis, and
    /// realized totals booked under <paramref name="firmId"/>. Master
    /// keepers are firm-keyed since #316; the legacy 2-arg overload
    /// above delegates to <see cref="PnlKeeper.DefaultFirmId"/> for
    /// callers that have not yet been threaded.
    /// </summary>
    public static PnlTodayDto Build(
        EndClientId owner,
        string firmId,
        PnlKeeper pnl,
        PositionKeeper positions,
        IReferencePrice refPrice,
        DateOnly? day = null)
    {
        var d = day ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        var unrealized = new List<PnlUnrealizedEntry>();
        decimal totalUnrealized = 0m;
        foreach (var p in positions.ForEndClientAndFirm(firmId, owner))
        {
            if (p.NetQuantity == 0) continue;
            if (pnl.GetUnknownBasisQty(firmId, owner.Value, p.Symbol) != 0) continue;
            if (!refPrice.TryGet(p.Symbol, out var px)) continue;
            var value = p.NetQuantity >= 0
                ? (px - p.AverageEntryPrice) * p.NetQuantity
                : (p.AverageEntryPrice - px) * (-p.NetQuantity);
            unrealized.Add(new PnlUnrealizedEntry(p.Symbol, value, px, p.NetQuantity, p.AverageEntryPrice));
            totalUnrealized += value;
        }

        var realized = new List<PnlRealizedEntry>();
        decimal totalRealized = 0m;
        foreach (var (symbol, value) in pnl.ForEndClientDay(firmId, owner.Value, d))
        {
            if (value == 0m) continue;
            realized.Add(new PnlRealizedEntry(symbol, value));
            totalRealized += value;
        }

        return new PnlTodayDto(realized, unrealized, totalRealized, totalUnrealized);
    }
}
