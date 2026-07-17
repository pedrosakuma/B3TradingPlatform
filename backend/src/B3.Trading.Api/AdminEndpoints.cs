using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
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
using Microsoft.Extensions.Logging;
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

        group.MapPost("/kill/end-client/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher, IAuditLogger audit) =>
            ToggleKill(dispatcher, audit, "end-client", id, killed: true, ctx, () => svc.KillEndClient(new EndClientId(id))));

        group.MapDelete("/kill/end-client/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher, IAuditLogger audit) =>
            ToggleKill(dispatcher, audit, "end-client", id, killed: false, ctx, () => svc.ReviveEndClient(new EndClientId(id))));

        group.MapPost("/kill/firm/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher, IAuditLogger audit) =>
            ToggleKill(dispatcher, audit, "firm", id, killed: true, ctx, () => svc.KillFirm(id)));

        group.MapDelete("/kill/firm/{id}", (string id, HttpContext ctx, KillSwitchService svc, EventDispatcher dispatcher, IAuditLogger audit) =>
            ToggleKill(dispatcher, audit, "firm", id, killed: false, ctx, () => svc.ReviveFirm(id)));

        // ── Symbol trading halts ─────────────────────────────────
        // Per-symbol pre-trade gate (#108 slice 2). Halts are
        // event-sourced via SymbolHaltToggledEvent so they survive
        // restart — losing a halt on crash would be the worst
        // possible default for a safety control.
        group.MapGet("/halts", (SymbolHaltService svc) =>
            Results.Ok(new
            {
                // Back-compat: the flat symbol list pre-#370 callers
                // (and existing tests) deserialise.
                Symbols = svc.ListHalted(),
                // #370 Stage A: additive per-symbol origin so the
                // operator UI can label a halt "operator" vs "venue"
                // vs both, and reason about who must clear it.
                Halts = svc.ListHaltedWithOrigin()
                    .Select(e => new { e.Symbol, Origin = HaltOriginLabel(e.Flags) }),
            }));

        group.MapPost("/halts/{symbol}", (string symbol, HttpContext ctx, SymbolHaltService svc, EventDispatcher dispatcher, IAuditLogger audit, ILoggerFactory loggerFactory) =>
            ToggleHalt(dispatcher, audit, svc, loggerFactory, symbol, halted: true, ctx));

        group.MapDelete("/halts/{symbol}", (string symbol, HttpContext ctx, SymbolHaltService svc, EventDispatcher dispatcher, IAuditLogger audit, ILoggerFactory loggerFactory) =>
            ToggleHalt(dispatcher, audit, svc, loggerFactory, symbol, halted: false, ctx));

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

        group.MapPost("/session-phase/default", (SessionPhasePayload req, HttpContext ctx, SessionPhaseService svc, EventDispatcher dispatcher, IAuditLogger audit) =>
            ChangeSessionPhase(dispatcher, audit, symbol: null, cleared: false, req?.Phase, ctx,
                phase => svc.SetDefaultPhase(phase),
                requirePhase: true));

        group.MapPost("/session-phase/{symbol}", (string symbol, SessionPhasePayload req, HttpContext ctx, SessionPhaseService svc, EventDispatcher dispatcher, IAuditLogger audit) =>
            ChangeSessionPhase(dispatcher, audit, symbol, cleared: false, req?.Phase, ctx,
                phase => svc.SetPhase(symbol, phase),
                requirePhase: true));

        group.MapDelete("/session-phase/{symbol}", (string symbol, HttpContext ctx, SessionPhaseService svc, EventDispatcher dispatcher, IAuditLogger audit) =>
            ChangeSessionPhase(dispatcher, audit, symbol, cleared: true, phaseStr: null, ctx,
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
            (string firmId, string clOrdId, MarkStaleRequest req, HttpContext ctx, OrderStalenessService svc, IAuditLogger audit) =>
            {
                if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                    return Results.NotFound();
                var reason = string.IsNullOrWhiteSpace(req?.Reason) ? "operator-marked-stale" : req!.Reason!;
                var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
                try
                {
                    // Pass-1 review (#322) P1.2. Audit-first ordering —
                    // emit the operator's stale-mark intent before the
                    // service call so a WAL-backpressured audit append
                    // refuses the mutation with 503 rather than letting
                    // the OrderStalenessService dispatch its own WAL
                    // event un-audited. The actual mark result
                    // (Marked/AlreadyStale/NotEligible/NotFound) is
                    // communicated by the HTTP response below; the
                    // audit envelope records the attempt.
                    EmitAdminConfigChange(audit, ctx, "/admin/orders/mark-stale", AuditOutcomes.Success, new()
                    {
                        ["firm"] = firmId,
                        ["cl_ord_id"] = clOrdId,
                        ["reason"] = reason,
                    }, failClosed: true);
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
            (string firmId, string clOrdId, HttpContext ctx, OrderStalenessService svc, IAuditLogger audit) =>
            {
                if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                    return Results.NotFound();
                var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
                try
                {
                    // Pass-1 review (#322) P1.2. Audit-first ordering —
                    // see mark-stale above.
                    EmitAdminConfigChange(audit, ctx, "/admin/orders/clear-stale", AuditOutcomes.Success, new()
                    {
                        ["firm"] = firmId,
                        ["cl_ord_id"] = clOrdId,
                    }, failClosed: true);
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

        group.MapPost("/eod", (IEodMaterialiser eod, HttpContext ctx, IAuditLogger audit) =>
        {
            // EOD materialisation runs against persisted segments, so it
            // is a no-op (and arguably misleading) when persistence is
            // disabled. Surface that as 409 rather than silently producing
            // an empty report.
            try
            {
                if (!eod.IsAvailable)
                {
                    // Pass-1 review (#322) P1.2. Up-front denial: audit
                    // the denied outcome with the precise reason and
                    // surface 409 — no business work to perform, so the
                    // single audit record carries the full picture.
                    EmitAdminConfigChange(audit, ctx, "/admin/eod", AuditOutcomes.Denied, new()
                    {
                        ["reason"] = "persistence_disabled",
                    }, failClosed: true);
                    return Results.Conflict(new { error = "persistence_disabled" });
                }
                // Audit-first ordering — record the operator's EOD
                // trigger before the (potentially expensive)
                // materialisation runs so a WAL-backpressured audit
                // append refuses the run with 503.
                EmitAdminConfigChange(audit, ctx, "/admin/eod", AuditOutcomes.Success, failClosed: true);
                var report = eod.Materialise(DateOnly.FromDateTime(DateTime.UtcNow));
                return Results.Ok(report);
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "admin.eod"));
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
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
        group.MapPost("/risk/reload", (IServiceProvider sp, HttpContext ctx, IAuditLogger audit) =>
        {
            try
            {
                // Pass-1 review (#322) P1.2. Audit-first ordering for
                // a risk-config reload: a backpressured audit append
                // refuses the reload with 503 rather than reloading
                // silently un-audited (this endpoint can flip
                // resolver behaviour platform-wide once a custom
                // provider is wired).
                EmitAdminConfigChange(audit, ctx, "/admin/risk/reload", AuditOutcomes.Success, new()
                {
                    ["reloader_present"] = sp.GetService<IRiskOptionsReloader>() is null ? "false" : "true",
                }, failClosed: true);
                var reloader = sp.GetService<IRiskOptionsReloader>();
                reloader?.Reload();
                return Results.NoContent();
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "admin.risk.reload"));
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
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

        // ── Cash ledger (Q2.2 / #269) ───────────────────────────
        // Admin-driven deposits and withdrawals, persisted as
        // CashLedgerEvent on the WAL and projected into CashKeeper.
        // Decoupled from ER fills — fill-driven cash deltas land via
        // the existing CashLedger and the future P&L engine (#271).
        group.MapPost("/cash", (CashLedgerRequest? req, HttpContext ctx, CashKeeper keeper, EventDispatcher dispatcher, IAuditLogger audit) =>
            HandleCashLedger(req, ctx, keeper, dispatcher, audit));

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

    private static IResult HandleCashLedger(
        CashLedgerRequest? req,
        HttpContext ctx,
        CashKeeper keeper,
        EventDispatcher dispatcher,
        IAuditLogger audit)
    {
        if (req is null)
            return Results.BadRequest(new { error = "request body required" });
        if (string.IsNullOrWhiteSpace(req.Endclient))
            return Results.BadRequest(new { error = "endclient required" });
        if (string.IsNullOrWhiteSpace(req.Kind))
            return Results.BadRequest(new { error = "kind required (Deposit|Withdrawal)" });

        var kind = req.Kind.Trim();
        if (!string.Equals(kind, "Deposit", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "Withdrawal", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "kind must be one of: Deposit, Withdrawal" });
        // Normalise to the canonical wire spelling so the WAL string is
        // stable regardless of caller casing.
        kind = string.Equals(kind, "Deposit", StringComparison.OrdinalIgnoreCase) ? "Deposit" : "Withdrawal";

        if (req.Amount <= 0m)
            return Results.BadRequest(new { error = "amount must be > 0" });

        var currency = (req.Currency ?? string.Empty).Trim().ToUpperInvariant();
        // v0 whitelist: BRL only. Multi-currency expands the list without
        // changing the wire shape (see CashLedgerEvent doc comment).
        if (currency != "BRL")
            return Results.BadRequest(new { error = "currency must be one of: BRL" });

        var owner = new EndClientId(req.Endclient);
        var operatorId = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        var firmId = ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";

        try
        {
            // Pass-1 review (#322) P1.2. Audit-first ordering — emit
            // the operator's cash-ledger intent BEFORE the dispatch
            // so a WAL-backpressured audit append refuses the
            // (cash-affecting) mutation with 503 rather than
            // committing it un-audited. For withdrawals the dispatch
            // below uses DispatchWithPreApply (atomic under the
            // snapshot lock) and may still be denied at runtime
            // (insufficient_funds); that downstream denial is
            // surfaced by the HTTP response and the cash counter —
            // the audit envelope records the attempt.
            EmitAdminConfigChange(audit, ctx, "/admin/cash", AuditOutcomes.Success, new()
            {
                ["endclient"] = req.Endclient!,
                ["firmId"] = firmId,
                ["kind"] = kind,
                ["amount"] = req.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["currency"] = currency,
                ["reference"] = req.Reference ?? "",
            }, failClosed: true);

            // Q2.2 (#269) P1 fix: debit + WAL append must be atomic with
            // respect to the snapshot lock. The previous flow ran
            // TryWithdraw OUTSIDE the dispatcher lock; a snapshot could
            // interleave between a successful debit and the WAL append,
            // persisting a reduced balance with no matching event and
            // permanently losing cash on restore. We now route the
            // withdrawal through DispatchWithPreApply so TryWithdraw,
            // the WAL append, and any rollback all run under the same
            // lock the snapshot service takes.
            if (kind == "Withdrawal")
            {
                var outcome = dispatcher.DispatchWithPreApply(
                    new CashLedgerEvent
                    {
                        EndClientId = req.Endclient,
                        FirmId = firmId,
                        Operation = kind,
                        Amount = req.Amount,
                        Currency = currency,
                        Reference = req.Reference,
                        OperatorId = operatorId,
                    },
                    preApply: () => keeper.TryWithdraw(firmId, owner, req.Amount),
                    rollback: () => keeper.ApplyDeposit(firmId, owner, req.Amount));

                if (!outcome.Applied)
                {
                    return Results.UnprocessableEntity(new
                    {
                        error = "insufficient_funds",
                        available = keeper.GetAvailable(firmId, owner),
                        requested = req.Amount,
                    });
                }
            }
            else
            {
                dispatcher.Dispatch(
                    new CashLedgerEvent
                    {
                        EndClientId = req.Endclient,
                        FirmId = firmId,
                        Operation = kind,
                        Amount = req.Amount,
                        Currency = currency,
                        Reference = req.Reference,
                        OperatorId = operatorId,
                    },
                    () => keeper.ApplyDeposit(firmId, owner, req.Amount));
            }

            return Results.Ok(new
            {
                endclient = req.Endclient,
                firmId,
                kind,
                amount = req.Amount,
                currency,
                available = keeper.GetAvailable(firmId, owner),
            });
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "admin.cash"));
            return Results.Json(
                new { error = "system busy (WAL backpressure)", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult ToggleKill(
        EventDispatcher dispatcher,
        IAuditLogger audit,
        string scope,
        string target,
        bool killed,
        HttpContext ctx,
        Action mutate)
    {
        var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        // Pass-1 review (#322) P1.2. Audit-first: emit the audit
        // envelope BEFORE the business dispatch so a WAL-backpressured
        // audit append refuses the mutation with 503 rather than
        // silently committing the kill-switch toggle un-audited. The
        // catch below converts the audit-site backpressure into a
        // structured 503; the inner dispatch's own backpressure is
        // converted by the same handler (the audit record then
        // documents the operator's attempt regardless).
        try
        {
            // Pass-2 review (#327) P1 — when scope=="firm" the
            // `target` slot carries another firm's id; emit it under
            // the `firm` key instead so compliance audit firm-touch
            // matching + cross-firm redaction (which only know about
            // FirmDetailKeys) covers kill-switch toggles. Non-firm
            // scopes (currently `endclient`) stay on `target`.
            var killDetails = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scope"] = scope,
                ["killed"] = killed ? "true" : "false",
            };
            if (string.Equals(scope, "firm", StringComparison.OrdinalIgnoreCase))
                killDetails["firm"] = target;
            else
                killDetails["target"] = target;
            EmitAdminConfigChange(audit, ctx, "/admin/kill", AuditOutcomes.Success, killDetails, failClosed: true);
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
        IAuditLogger audit,
        SymbolHaltService svc,
        ILoggerFactory loggerFactory,
        string symbol,
        bool halted,
        HttpContext ctx)
    {
        var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        try
        {
            // Pass-1 review (#322) P1.2. Audit-first ordering — see
            // ToggleKill for the rationale.
            EmitAdminConfigChange(audit, ctx, "/admin/halts", AuditOutcomes.Success, new()
            {
                ["symbol"] = symbol,
                ["halted"] = halted ? "true" : "false",
            }, failClosed: true);
            dispatcher.Dispatch(
                new SymbolHaltToggledEvent
                {
                    Symbol = symbol,
                    Halted = halted,
                    ActorUserId = actor,
                    Origin = HaltOrigin.Operator,
                },
                // The operator surface only ever touches the operator
                // origin flag; a venue halt observed via market data is
                // independent (see SymbolHaltService / HaltOrigin).
                () =>
                {
                    if (halted) svc.Halt(symbol, HaltOrigin.Operator);
                    else svc.Resume(symbol, HaltOrigin.Operator);
                });
            MetricsRegistry.SymbolHaltToggled.Add(1,
                new KeyValuePair<string, object?>("halted", halted),
                new KeyValuePair<string, object?>("origin", "operator"));

            // #370 Stage A exit criterion: an operator resume clears
            // only the operator flag. If the venue still has the symbol
            // halted, the symbol stays halted — warn loudly and tell the
            // caller so nobody assumes the ticket is tradeable again.
            if (!halted && svc.IsHaltedBy(symbol, HaltOrigin.Venue))
            {
                loggerFactory
                    .CreateLogger("B3.Trading.Api.AdminEndpoints.Halts")
                    .LogWarning(
                        "Operator resume for {Symbol} cleared the operator halt, but the venue still has it halted; the symbol remains halted until the venue resumes.",
                        symbol);
                return Results.Ok(new
                {
                    symbol,
                    resumed = false,
                    stillHaltedBy = "venue",
                    detail = "Operator halt cleared, but the venue still has this symbol halted. It remains halted until the venue resumes.",
                });
            }
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

    /// <summary>
    /// Maps the <see cref="SymbolHaltEntry.Flags"/> bitmask
    /// (Operator=1, Venue=2) to a stable label for the admin halt
    /// listing. "operator+venue" means both origins hold the halt and
    /// it stays halted until both clear.
    /// </summary>
    private static string HaltOriginLabel(byte flags)
    {
        var hasOperator = (flags & (1 << (int)HaltOrigin.Operator)) != 0;
        var hasVenue = (flags & (1 << (int)HaltOrigin.Venue)) != 0;
        return (hasOperator, hasVenue) switch
        {
            (true, true) => "operator+venue",
            (false, true) => "venue",
            _ => "operator",
        };
    }
    private static IResult ChangeSessionPhase(
        EventDispatcher dispatcher,
        IAuditLogger audit,
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
            // Pass-1 review (#322) P1.2. Audit-first — emit the
            // operator's intent before the business dispatch so a
            // WAL-backpressured audit append refuses the phase change
            // with 503 rather than silently committing it un-audited.
            EmitAdminConfigChange(audit, ctx, "/admin/session-phase", AuditOutcomes.Success, new()
            {
                ["scope"] = string.IsNullOrWhiteSpace(symbol) ? "default" : "symbol",
                ["symbol"] = symbol ?? "",
                ["cleared"] = cleared ? "true" : "false",
                ["phase"] = cleared ? "cleared" : parsed.ToString(),
            }, failClosed: true);
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

    /// <summary>
    /// Q4.5 (#305). Single audit-emit site for the admin mutating
    /// endpoints. Centralised so the (actor, ip, firm, role)
    /// extraction is uniform and a future field add lands in one
    /// place. The caller is responsible for the
    /// <paramref name="details"/> map's call-site-specific keys
    /// (target id, before/after value, etc.).
    ///
    /// <para>Pass-1 review (#322) P1.2. When
    /// <paramref name="failClosed"/> is <c>true</c> the call routes
    /// through <see cref="IAuditLogger.LogOrFail"/> so a
    /// WAL-backpressured audit append propagates a
    /// <see cref="WalBackpressureException"/> the endpoint can
    /// convert to HTTP 503; callers MUST emit BEFORE the business
    /// dispatch when using this mode (audit-first ordering). The
    /// default best-effort mode is retained for tail-audit emits on
    /// paths where the operator decision has already been
    /// communicated (e.g. denial branches).</para>
    /// </summary>
    internal static void EmitAdminConfigChange(
        IAuditLogger audit,
        HttpContext ctx,
        string resourcePath,
        string outcome,
        Dictionary<string, string>? details = null,
        string eventType = AuditEventTypes.AdminConfigChange,
        string? reasonCode = null,
        bool failClosed = false)
    {
        var evt = new AuditLogEvent
        {
            EventType = eventType,
            Outcome = outcome,
            ActorUserId = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub),
            ActorUsername = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub),
            ActorFirm = ctx.User.FindFirstValue(JwtIssuer.FirmClaim),
            ActorRole = ctx.User.FindFirstValue(JwtIssuer.RoleClaim),
            SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = resourcePath,
            ReasonCode = reasonCode,
            Details = details,
        };
        if (failClosed)
            audit.LogOrFail(evt);
        else
            audit.Log(evt);
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

/// <summary>
/// Body for <c>POST /admin/cash</c> (Q2.2 / #269). Operator-driven
/// deposit or withdrawal. <see cref="Kind"/> is <c>"Deposit"</c> or
/// <c>"Withdrawal"</c> (case-insensitive); <see cref="Amount"/> is
/// strictly positive (sign is implied by Kind); <see cref="Currency"/>
/// is whitelisted to <c>"BRL"</c> in v0.
/// </summary>
public sealed class CashLedgerRequest
{
    public string? Endclient { get; set; }
    public string? Kind { get; set; }
    public decimal Amount { get; set; }
    public string? Currency { get; set; }
    public string? Reference { get; set; }
}
