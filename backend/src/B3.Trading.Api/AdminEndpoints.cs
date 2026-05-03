using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("admin");

        group.MapGet("/kill", (KillSwitchService svc) => Results.Ok(new
        {
            EndClients = svc.ListKilledEndClients(),
            Firms = svc.ListKilledFirms(),
        }));

        group.MapPost("/kill/end-client/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher) =>
            ToggleKill(dispatcher, "end-client", id, killed: true, ctx, () => svc.KillEndClient(new EndClientId(id))));

        group.MapDelete("/kill/end-client/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher) =>
            ToggleKill(dispatcher, "end-client", id, killed: false, ctx, () => svc.ReviveEndClient(new EndClientId(id))));

        group.MapPost("/kill/firm/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher) =>
            ToggleKill(dispatcher, "firm", id, killed: true, ctx, () => svc.KillFirm(id)));

        group.MapDelete("/kill/firm/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher) =>
            ToggleKill(dispatcher, "firm", id, killed: false, ctx, () => svc.ReviveFirm(id)));

        group.MapPost("/eod", (EodMaterialiser eod, IOptions<PersistenceOptions> opts) =>
        {
            // EOD materialisation runs against persisted segments, so it
            // is a no-op (and arguably misleading) when persistence is
            // disabled. Surface that as 409 rather than silently producing
            // an empty report.
            if (!opts.Value.Enabled)
                return Results.Conflict(new { error = "persistence_disabled" });
            var report = eod.Materialise(DateOnly.FromDateTime(DateTime.UtcNow));
            return Results.Ok(report);
        });

        // Per-firm operator visibility. In Real mode the response folds in
        // live FIXP state from the FirmGatewayRegistry; in other modes it
        // returns the configured shape only (state fields are null) — useful
        // both as a config sanity check and as a stable schema for dashboards.
        group.MapGet("/firms", (IOptions<ExchangeOptions> opts, IServiceProvider sp) =>
        {
            var mode = opts.Value.ResolveMode();
            // Optional injection: FirmGatewayRegistry is only registered in Real mode.
            var registry = sp.GetService<FirmGatewayRegistry>();
            var firms = opts.Value.Firms.Select(cfg =>
            {
                B3EntryPointClientGateway? live = null;
                if (registry is not null && registry.TryGet(cfg.FirmId, out var gw))
                    live = gw;
                return new
                {
                    firmId = cfg.FirmId,
                    endpoint = cfg.Endpoint,
                    sessionId = cfg.SessionId,
                    sessionState = live?.SessionStateTag,
                    sessionVerId = live?.CurrentSessionVerId,
                    reconnecting = live?.IsReconnecting,
                };
            }).ToArray();
            return Results.Ok(new { mode = mode.ToString(), firms });
        });

        // Debug helper for ops: surface the *effective* RiskLimits the
        // resolver picks for a given (endClient, firm, symbol) tuple.
        // Pure read — no side effects. The caller passes whatever
        // dimension(s) they care about; missing values default to a
        // sentinel that matches no entry so the resolver falls through
        // to per-symbol/default for that slot.
        group.MapGet("/risk/limits", (IOptionsMonitor<RiskOptions> opts, string? endClient, string? firmId, string? symbol) =>
        {
            var resolved = RiskLimitsResolver.ResolveAll(
                opts.CurrentValue,
                endClient ?? string.Empty,
                firmId,
                symbol ?? string.Empty);
            return Results.Ok(new
            {
                query = new { endClient, firmId, symbol },
                limits = new
                {
                    maxQuantity = resolved.MaxQuantity,
                    maxNotional = resolved.MaxNotional,
                    priceCollarPercent = resolved.PriceCollarPercent,
                    priceCollarAbsolute = resolved.PriceCollarAbsolute,
                    positionLimit = resolved.PositionLimit,
                },
            });
        });

        // Reload hook for non-appsettings configuration providers.
        // The default appsettings provider already watches the file
        // and pushes IOptionsMonitor.OnChange notifications, so this
        // endpoint is a no-op there. When a future provider (file/DB
        // adapter from the persistence spike) ships, it can plug in
        // an IRiskOptionsReloader and have the body trigger an
        // out-of-band reload. Returns 204 either way; the caller can
        // immediately re-query /admin/risk/limits to verify.
        group.MapPost("/risk/reload", (IServiceProvider sp) =>
        {
            var reloader = sp.GetService<IRiskOptionsReloader>();
            reloader?.Reload();
            return Results.NoContent();
        });

        return app;
    }

    private static IResult ToggleKill(
        EventDispatcher dispatcher,
        string scope,
        string target,
        bool killed,
        HttpContext ctx,
        Action mutate)
    {
        var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        try
        {
            dispatcher.Dispatch(
                new KillSwitchToggledEvent
                {
                    Scope = scope,
                    Target = target,
                    Killed = killed,
                    ActorUserId = actor,
                },
                mutate);
            MetricsRegistry.KillSwitchToggled.Add(1,
                new KeyValuePair<string, object?>("scope", scope),
                new KeyValuePair<string, object?>("killed", killed));
            return Results.NoContent();
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "admin.kill"));
            return Results.Json(
                new { error = "system busy (WAL backpressure)", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
