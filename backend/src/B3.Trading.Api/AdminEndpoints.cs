using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
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

        // ── Symbol trading halts ─────────────────────────────────
        // Per-symbol pre-trade gate (#108 slice 2). Halts are
        // event-sourced via SymbolHaltToggledEvent so they survive
        // restart — losing a halt on crash would be the worst
        // possible default for a safety control.
        group.MapGet("/halts", (SymbolHaltService svc) =>
            Results.Ok(new { Symbols = svc.ListHalted() }));

        group.MapPost("/halts/{symbol}", (string symbol, HttpContext ctx, SymbolHaltService svc, EventDispatcher dispatcher) =>
            ToggleHalt(dispatcher, symbol, halted: true, ctx, () => svc.Halt(symbol)));

        group.MapDelete("/halts/{symbol}", (string symbol, HttpContext ctx, SymbolHaltService svc, EventDispatcher dispatcher) =>
            ToggleHalt(dispatcher, symbol, halted: false, ctx, () => svc.Resume(symbol)));

        // ── Session phase (#108) ──────────────────────────────────
        // Per-symbol override + global default trading phase. Drives
        // SessionPhaseCheck; auctions reject Market, Closed rejects
        // everything. Persisted via SessionPhaseChangedEvent so a
        // restart restores the last known restriction — losing a
        // non-Continuous phase on crash would silently revert to the
        // least restrictive mode.
        group.MapGet("/session-phase", (SessionPhaseService svc) => Results.Ok(new
        {
            Default = svc.DefaultPhase.ToString(),
            Overrides = svc.ListOverrides().ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
        }));

        group.MapPost("/session-phase/default", (SessionPhasePayload req, HttpContext ctx, SessionPhaseService svc, EventDispatcher dispatcher) =>
            ChangeSessionPhase(dispatcher, symbol: null, cleared: false, req?.Phase, ctx,
                phase => svc.SetDefaultPhase(phase),
                requirePhase: true));

        group.MapPost("/session-phase/{symbol}", (string symbol, SessionPhasePayload req, HttpContext ctx, SessionPhaseService svc, EventDispatcher dispatcher) =>
            ChangeSessionPhase(dispatcher, symbol, cleared: false, req?.Phase, ctx,
                phase => svc.SetPhase(symbol, phase),
                requirePhase: true));

        group.MapDelete("/session-phase/{symbol}", (string symbol, HttpContext ctx, SessionPhaseService svc, EventDispatcher dispatcher) =>
            ChangeSessionPhase(dispatcher, symbol, cleared: true, phaseStr: null, ctx,
                _ => svc.ClearPhase(symbol),
                requirePhase: false));

        // ── Order staleness overlay (#132 slice 1) ────────────────
        // Lets an admin flag a specific working order as
        // suspected-stale-by-venue (matching restart, FIXP gap, etc.)
        // so Cancel/Modify return 409 until it's cleared. Cleared
        // automatically when a real terminal ER arrives. Both routes
        // are firm-scoped so the same ClOrdID across firms (rare —
        // ClOrdIDs are per-firm) cannot be addressed from another firm.
        group.MapPost("/firms/{firmId}/orders/{clOrdId}/mark-stale",
            (string firmId, string clOrdId, MarkStaleRequest req, HttpContext ctx, OrderStalenessService svc) =>
            {
                if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                    return Results.NotFound();
                var reason = string.IsNullOrWhiteSpace(req?.Reason) ? "operator-marked-stale" : req!.Reason!;
                var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
                try
                {
                    var result = svc.MarkStale(firmId, clOrdIdU, reason, DateTimeOffset.UtcNow, actor);
                    return result switch
                    {
                        MarkStaleResult.Marked => Results.NoContent(),
                        MarkStaleResult.AlreadyStale => Results.NoContent(),
                        MarkStaleResult.NotFound => Results.NotFound(),
                        MarkStaleResult.WrongFirm => Results.NotFound(),
                        MarkStaleResult.NotEligible => Results.Conflict(new { error = "order not eligible for stale mark (must be Working or PartiallyFilled)" }),
                        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                    };
                }
                catch (WalBackpressureException ex)
                {
                    MetricsRegistry.WalBackpressure.Add(1,
                        new KeyValuePair<string, object?>("call_site", "admin.stale.mark"));
                    return Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = ex.Message },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        group.MapPost("/firms/{firmId}/orders/{clOrdId}/clear-stale",
            (string firmId, string clOrdId, HttpContext ctx, OrderStalenessService svc) =>
            {
                if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                    return Results.NotFound();
                var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
                try
                {
                    var result = svc.ClearStale(firmId, clOrdIdU, actor);
                    return result switch
                    {
                        ClearStaleResult.Cleared => Results.NoContent(),
                        ClearStaleResult.NotStale => Results.NoContent(),
                        ClearStaleResult.NotFound => Results.NotFound(),
                        ClearStaleResult.WrongFirm => Results.NotFound(),
                        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                    };
                }
                catch (WalBackpressureException ex)
                {
                    MetricsRegistry.WalBackpressure.Add(1,
                        new KeyValuePair<string, object?>("call_site", "admin.stale.clear"));
                    return Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = ex.Message },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        group.MapPost("/eod", (IEodMaterialiser eod) =>
        {
            // EOD materialisation runs against persisted segments, so it
            // is a no-op (and arguably misleading) when persistence is
            // disabled. Surface that as 409 rather than silently producing
            // an empty report.
            if (!eod.IsAvailable)
                return Results.Conflict(new { error = "persistence_disabled" });
            var report = eod.Materialise(DateOnly.FromDateTime(DateTime.UtcNow));
            return Results.Ok(report);
        });

        // Per-firm operator visibility. In Real mode the response folds in
        // live FIXP state from the firm directory; in other modes it
        // returns the configured shape only (state fields are null) — useful
        // both as a config sanity check and as a stable schema for dashboards.
        group.MapGet("/firms", (IFirmDirectory directory) =>
        {
            var snapshot = directory.Snapshot();
            var firms = snapshot.Firms.Select(f => new
            {
                firmId = f.FirmId,
                endpoint = f.Endpoint,
                sessionId = f.SessionId,
                sessionState = f.SessionState,
                sessionVerId = f.SessionVerId,
                reconnecting = f.Reconnecting,
            }).ToArray();
            return Results.Ok(new { mode = snapshot.Mode, firms });
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
            IFirmDirectory firmDirectory) =>
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
                mode = firmDirectory.Snapshot().Mode,
                marketDataEnabled = mdRef is not null,
                symbols = items,
            });
        });

        // POST /admin/simulator/er — synthetic ER injection (formerly the
        // ExchangeMode.Simulator-only route; merged into Mock+AllowErInjection
        // in #163). The route itself moved to the Infrastructure project as
        // part of the #188 layering refactor — see SimulatorEndpoint.MapSimulatorEndpoints,
        // which the Host composition root mounts conditionally on
        // Trading:Exchange:Mode=Mock + AllowErInjection. Kept out of this
        // file so the Api project no longer references Infrastructure.
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

    private static IResult ToggleHalt(
        EventDispatcher dispatcher,
        string symbol,
        bool halted,
        HttpContext ctx,
        Action mutate)
    {
        var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        try
        {
            dispatcher.Dispatch(
                new SymbolHaltToggledEvent
                {
                    Symbol = symbol,
                    Halted = halted,
                    ActorUserId = actor,
                },
                mutate);
            MetricsRegistry.SymbolHaltToggled.Add(1,
                new KeyValuePair<string, object?>("halted", halted));
            return Results.NoContent();
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "admin.halts"));
            return Results.Json(
                new { error = "system busy (WAL backpressure)", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    private static IResult ChangeSessionPhase(
        EventDispatcher dispatcher,
        string? symbol,
        bool cleared,
        string? phaseStr,
        HttpContext ctx,
        Action<SessionPhase> mutate,
        bool requirePhase)
    {
        SessionPhase parsed = SessionPhase.Continuous;
        if (requirePhase)
        {
            if (string.IsNullOrWhiteSpace(phaseStr) || !Enum.TryParse(phaseStr, ignoreCase: true, out parsed))
                return Results.BadRequest(new { error = "phase must be one of: Closed, PreOpening, OpeningAuction, Continuous, ClosingAuction, AfterHours" });
        }

        var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        try
        {
            dispatcher.Dispatch(
                new SessionPhaseChangedEvent
                {
                    Symbol = symbol,
                    Phase = parsed.ToString(),
                    Cleared = cleared,
                    ActorUserId = actor,
                },
                () => mutate(parsed));
            MetricsRegistry.SessionPhaseChanged.Add(1,
                new KeyValuePair<string, object?>("scope", string.IsNullOrWhiteSpace(symbol) ? "default" : "symbol"),
                new KeyValuePair<string, object?>("phase", cleared ? "cleared" : parsed.ToString()));
            return Results.NoContent();
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "admin.session-phase"));
            return Results.Json(
                new { error = "system busy (WAL backpressure)", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

/// <summary>
/// Body for <c>POST /admin/session-phase[/{symbol}|/default]</c> (#108).
/// </summary>
public sealed class SessionPhasePayload
{
    public string? Phase { get; set; }
}

/// <summary>
/// Body for <c>POST /admin/firms/{firmId}/orders/{clOrdId}/mark-stale</c> (#132 slice 1).
/// </summary>
public sealed record MarkStaleRequest(string? Reason);
