using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Application.MarketData;
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
                    minNotional = resolved.MinNotional,
                    priceCollarPercent = resolved.PriceCollarPercent,
                    priceCollarAbsolute = resolved.PriceCollarAbsolute,
                    positionLimit = resolved.PositionLimit,
                    maxOpenOrders = resolved.MaxOpenOrders,
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

        // GET /admin/marketdata/reference-prices?symbols=ITUB4,VALE3
        // Operator/diagnostics view of the reference-price plumbing.
        // Surfaces three independent readings per symbol so the caller
        // can disambiguate "live deslocou fallback?" without inferring
        // it from the metric tags alone:
        //   - effective : what IReferencePrice.Lookup currently returns
        //                 (Live | Fallback | Missing) — the single value
        //                 the price-collar check actually consumes.
        //   - live      : raw entry from MarketDataReferencePrice's cache
        //                 (price + updatedUtc), independent of staleness.
        //                 Null when MD feature is off or the symbol has
        //                 never been observed on the WS feed.
        //   - fallback  : raw entry from the static config table.
        //                 Null when the symbol has no static reference.
        // When `symbols` is omitted, returns the union of MD-subscribed
        // symbols (Trading:MarketData:Symbols) and statically-configured
        // ones (Trading:Risk:ReferencePrices), de-duplicated.
        group.MapGet("/marketdata/reference-prices", (
            string? symbols,
            ConfigReferencePrice fallback,
            IServiceProvider sp,
            IOptions<MarketDataOptions> mdOpts,
            IOptionsMonitor<RiskOptions> riskOpts,
            IOptions<ExchangeOptions> exchangeOpts) =>
        {
            // MarketDataReferencePrice is registered only when the WsUrl
            // gate is set (see MarketDataRegistration.cs). When absent,
            // IReferencePrice resolves to ConfigReferencePrice — same as
            // the fallback we already inject here, so the endpoint
            // gracefully degrades to a "fallback only" view.
            var mdRef = sp.GetService<MarketDataReferencePrice>();
            var liveSnapshot = mdRef?.Snapshot();
            var effective = (IReferencePrice?)mdRef ?? fallback;

            var requested = ParseSymbolList(symbols);
            if (requested.Count == 0)
            {
                var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in mdOpts.Value.Symbols)
                    if (!string.IsNullOrWhiteSpace(s)) union.Add(s.Trim());
                foreach (var s in riskOpts.CurrentValue.ReferencePrices.Keys)
                    if (!string.IsNullOrWhiteSpace(s)) union.Add(s);
                requested = union.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            }

            var items = requested.Select(sym =>
            {
                var eff = effective.Lookup(sym);
                var fb = fallback.Lookup(sym);
                object? liveBlock = null;
                if (liveSnapshot is not null && liveSnapshot.TryGetValue(sym, out var entry))
                {
                    liveBlock = new
                    {
                        price = entry.Price,
                        updatedUtc = entry.UpdatedUtc,
                    };
                }
                return new
                {
                    symbol = sym,
                    effectivePrice = eff.Found ? eff.Price : (decimal?)null,
                    effectiveSource = eff.Source.ToString(),
                    live = liveBlock,
                    fallbackPrice = fb.Found ? fb.Price : (decimal?)null,
                };
            }).ToArray();

            return Results.Ok(new
            {
                mode = exchangeOpts.Value.ResolveMode().ToString(),
                marketDataEnabled = mdRef is not null,
                symbols = items,
            });
        });

        // POST /admin/simulator/er — synthetic ER injection for slice-4
        // simulator mode (RFC algo-orders-v0 §4.10/§7-B3). Only mapped
        // when Mode=Simulator at boot, so the route is invisible to other
        // deployments. A second runtime barrier (404 if mode flips
        // unexpectedly) is intentionally omitted because mode is fixed
        // at startup; the not-mapped check is the single source of truth.
        var modeAtBoot = app.ServiceProvider.GetRequiredService<IOptions<ExchangeOptions>>().Value.ResolveMode();
        if (modeAtBoot == ExchangeMode.Simulator)
        {
            group.MapPost("/simulator/er", SimulatorEndpoint.Inject);
        }

        return app;
    }

    private static List<string> ParseSymbolList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(raw))
                ordered.Add(raw);
        }
        return ordered;
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
