using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
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
        var group = app.MapGroup("/api/admin").RequireAuthorization("admin");

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
                    EmitAdminConfigChange(audit, ctx, "/api/admin/orders/mark-stale", AuditOutcomes.Success, new()
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
                    EmitAdminConfigChange(audit, ctx, "/api/admin/orders/clear-stale", AuditOutcomes.Success, new()
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
                    EmitAdminConfigChange(audit, ctx, "/api/admin/eod", AuditOutcomes.Denied, new()
                    {
                        ["reason"] = "persistence_disabled",
                    }, failClosed: true);
                    return Results.Conflict(new { error = "persistence_disabled" });
                }
                // Audit-first ordering — record the operator's EOD
                // trigger before the (potentially expensive)
                // materialisation runs so a WAL-backpressured audit
                // append refuses the run with 503.
                EmitAdminConfigChange(audit, ctx, "/api/admin/eod", AuditOutcomes.Success, failClosed: true);
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
        // immediately re-query /api/admin/risk/limits to verify.
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
                EmitAdminConfigChange(audit, ctx, "/api/admin/risk/reload", AuditOutcomes.Success, new()
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

        // GET /api/admin/marketdata/reference-prices?symbols=ITUB4,VALE3
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
        group.MapPost("/cash", (CashLedgerRequest? req, HttpContext ctx, CashKeeper keeper, CashLedger cashLedger, EventDispatcher dispatcher, IAuditLogger audit) =>
            HandleCashLedger(req, ctx, keeper, cashLedger, dispatcher, audit));

        // ── Position adjustment (#671/#753 RFC, PR 1) ───────────
        // Admin-driven ABSOLUTE position overwrite, persisted as
        // PositionAdjustmentEvent on the WAL and projected into
        // PositionKeeper (SetAbsolute), PnlKeeper (SetAbsoluteAvgCost),
        // AND SubAccountPnlKeeper's MASTER bucket only
        // (SetAbsoluteMasterBucketAvgCost — code-review addendum #2:
        // v1 adjustment is account-wide/master-only, never a named
        // sub-account bucket) in the same dispatcher-serialised apply
        // so the avg-cost basis never drifts from the position it is
        // derived from. Mirrors the /cash pattern above: FirmId is
        // always derived from the caller's JWT firm claim, never
        // accepted from the request body.
        group.MapPost("/positions", (PositionAdjustmentRequest? req, HttpContext ctx, PositionKeeper positions, PnlKeeper pnl, SubAccountPnlKeeper subAccountPnl, EventDispatcher dispatcher, IAuditLogger audit) =>
            HandlePositionAdjustment(req, ctx, positions, pnl, subAccountPnl, dispatcher, audit));

        // ── Whole-account reset (#671/#753 RFC, PR 3) ───────────
        // Admin-driven ATOMIC whole end-client account reset,
        // persisted as a SINGLE durable AccountResetEvent (never a
        // sequence of cash/position events — a crash between
        // separate events could expose a half-reset account) and
        // projected across CashKeeper, CashLedger, PositionKeeper,
        // PnlKeeper, and SubAccountPnlKeeper (every bucket cleared) in
        // the same dispatcher-serialised apply. Fails closed with 409
        // while the account has any working order OR any non-terminal
        // (or reconciliation-pending) outbound mutation; the guard is
        // re-evaluated INSIDE the same pre-apply critical region used
        // to serialize outbound mutation dispatch, so a concurrent
        // order submission cannot race the reset (TOCTOU-safe). Sub-
        // account reset is out of scope — this always targets the
        // whole end-client account.
        group.MapPost("/accounts/{endClientId}/reset", (
                string endClientId,
                HttpContext ctx,
                PositionKeeper positions,
                PnlKeeper pnl,
                SubAccountPnlKeeper subAccountPnl,
                SubAccountPositionKeeper subAccountPositions,
                CashKeeper cashKeeper,
                CashLedger cashLedger,
                WorkingOrderBook orders,
                OutboundMutationLedger outboundLedger,
                IOutboundCommandProtector commandProtector,
                IMarginProvider marginProvider,
                IOptions<CashSeedOptions> cashSeeds,
                IOptions<PositionSeedOptions> positionSeeds,
                EventDispatcher dispatcher,
                IAuditLogger audit) =>
            HandleAccountReset(
                endClientId, ctx, positions, pnl, subAccountPnl, subAccountPositions, cashKeeper, cashLedger,
                orders, outboundLedger, commandProtector, marginProvider,
                cashSeeds.Value, positionSeeds.Value, dispatcher, audit));

        // POST /api/admin/simulator/er — synthetic ER injection (formerly the
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
        CashLedger cashLedger,
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
            EmitAdminConfigChange(audit, ctx, "/api/admin/cash", AuditOutcomes.Success, new()
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
                    preApply: () =>
                    {
                        // #679. CashKeeper stays the authoritative
                        // insufficient-funds gate (unchanged semantics,
                        // preserves CashWithdrawalAtomicityTests); once
                        // approved, mirror the debit onto CashLedger so
                        // the spendable/margin balance stops diverging
                        // from the operator-facing counter.
                        if (!keeper.TryWithdraw(firmId, owner, req.Amount))
                            return false;
                        cashLedger.ApplyWithdrawal(firmId, owner, req.Amount);
                        return true;
                    },
                    rollback: () =>
                    {
                        keeper.ApplyDeposit(firmId, owner, req.Amount);
                        cashLedger.ApplyDeposit(firmId, owner, req.Amount);
                    });

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
                    () =>
                    {
                        keeper.ApplyDeposit(firmId, owner, req.Amount);
                        cashLedger.ApplyDeposit(firmId, owner, req.Amount);
                    });
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

    /// <summary>
    /// #671/#753 (RFC: admin account reset + runtime position adjustment,
    /// PR 1). Handles <c>POST /api/admin/positions</c> — an operator-driven
    /// ABSOLUTE position overwrite, mirroring <see cref="HandleCashLedger"/>
    /// exactly (audit-first ordering, WAL-backpressure 503, FirmId always
    /// derived from the JWT firm claim rather than the request body).
    /// Unlike the cash-withdrawal path there is no insufficient-funds
    /// failure mode here — every failure mode (missing fields, invariant
    /// violation) is a 400 checked BEFORE the audit emit / dispatch, so a
    /// plain <see cref="EventDispatcher.Dispatch(Persistence.WalEvent, Action)"/>
    /// suffices (no <c>DispatchWithPreApply</c> rollback branch needed).
    /// <see cref="PositionKeeper"/>, <see cref="PnlKeeper"/>, AND
    /// <see cref="SubAccountPnlKeeper"/>'s MASTER bucket (code-review
    /// addendum #2 — v1 adjustment is account-wide/master-only; no named
    /// sub-account bucket is ever fabricated or altered here) are all
    /// updated inside the SAME apply delegate so no keeper's avg-cost
    /// basis observably lags the position it derives from.
    /// </summary>
    private static IResult HandlePositionAdjustment(
        PositionAdjustmentRequest? req,
        HttpContext ctx,
        PositionKeeper positions,
        PnlKeeper pnl,
        SubAccountPnlKeeper subAccountPnl,
        EventDispatcher dispatcher,
        IAuditLogger audit)
    {
        if (req is null)
            return Results.BadRequest(new { error = "request body required" });
        if (string.IsNullOrWhiteSpace(req.Endclient))
            return Results.BadRequest(new { error = "endclient required" });
        if (string.IsNullOrWhiteSpace(req.Symbol))
            return Results.BadRequest(new { error = "symbol required" });
        // Code-review addendum (#671/#753 PR 1). NetQuantity/AverageEntryPrice
        // are nullable at the JSON-binding level specifically so an omitted
        // field is distinguishable from an explicit 0 — see the DTO doc
        // comment on PositionAdjustmentRequest. Both are semantically
        // required: there is no sensible default for an absolute overwrite.
        if (req.NetQuantity is null)
            return Results.BadRequest(new { error = "netQuantity required" });
        if (req.AverageEntryPrice is null)
            return Results.BadRequest(new { error = "averageEntryPrice required" });

        var netQuantity = req.NetQuantity.Value;
        var averageEntryPrice = req.AverageEntryPrice.Value;

        // RFC #753 invariant: a flat (zero) position carries a zero
        // average entry price; a non-flat position requires a strictly
        // positive average entry price. PositionKeeper.SetAbsolute /
        // PnlKeeper.SetAbsoluteAvgCost re-check this as defense-in-depth
        // (also guards WAL replay of a corrupted segment), but the
        // primary 400 gate lives here so the operator gets an immediate,
        // request-scoped error.
        if (netQuantity == 0 && averageEntryPrice != 0m)
            return Results.BadRequest(new { error = "averageEntryPrice must be 0 when netQuantity is 0" });
        if (netQuantity != 0 && averageEntryPrice <= 0m)
            return Results.BadRequest(new { error = "averageEntryPrice must be > 0 when netQuantity is non-zero" });

        var owner = new EndClientId(req.Endclient);
        var operatorId = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        // RFC #753 product decision: admin operations are scoped to the
        // administrator's JWT firm. No explicit cross-firm firmId
        // request parameter in v1 — never trust a client-supplied firm.
        //
        // Code-review addendum (#671/#753 PR 1). FAIL CLOSED when the
        // firm claim is missing or blank rather than silently defaulting
        // to the DEFAULT tenant bucket: unlike the read-only endpoints
        // elsewhere in this file that default to "default" for
        // legacy/no-firm compatibility, this is a durable, tenant-scoped
        // WRITE — misattributing it to the wrong (or a shared "default")
        // firm bucket because a token happened to be missing its firm
        // claim would be a silent cross-tenant data-integrity issue, not
        // a benign read fallback. A normally-issued admin JWT (see
        // JwtIssuer.Issue) always carries this claim; a token missing it
        // is malformed/forged and must be rejected outright.
        var firmIdClaim = ctx.User.FindFirstValue(JwtIssuer.FirmClaim);
        if (string.IsNullOrWhiteSpace(firmIdClaim))
        {
            return Results.Json(
                new { error = "firm claim missing or blank on caller JWT" },
                statusCode: StatusCodes.Status401Unauthorized);
        }
        var firmId = firmIdClaim;

        try
        {
            // Pass-1 review (#322) P1.2 pattern, reused here: audit-first
            // ordering — emit the operator's position-adjustment intent
            // BEFORE the dispatch so a WAL-backpressured audit append
            // refuses the mutation with 503 rather than committing it
            // un-audited.
            EmitAdminConfigChange(audit, ctx, "/api/admin/positions", AuditOutcomes.Success, new()
            {
                ["endclient"] = req.Endclient!,
                ["firmId"] = firmId,
                ["symbol"] = req.Symbol!,
                ["netQuantity"] = netQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["averageEntryPrice"] = averageEntryPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reference"] = req.Reference ?? "",
            }, failClosed: true);

            dispatcher.Dispatch(
                new PositionAdjustmentEvent
                {
                    EndClientId = req.Endclient,
                    FirmId = firmId,
                    Symbol = req.Symbol,
                    NetQuantity = netQuantity,
                    AverageEntryPrice = averageEntryPrice,
                    Reference = req.Reference,
                    OperatorId = operatorId,
                },
                () =>
                {
                    // All three keepers are updated in this single apply
                    // delegate — the dispatcher serialises it exactly
                    // like the live fill path (PositionKeeper.ApplyFill
                    // + PnlKeeper.ApplyFillToAvgCost +
                    // SubAccountPnlKeeper.ApplyBucketFill in
                    // ExecutionReportProcessor) — so a reader can never
                    // observe the position overwritten with a basis
                    // still reflecting the pre-adjustment state, or
                    // vice versa. SubAccountPnlKeeper only ever has its
                    // MASTER bucket touched here (code-review addendum
                    // #2) — v1 adjustment is account-wide/master-only,
                    // never a named sub-account bucket.
                    positions.SetAbsolute(firmId, owner, req.Symbol!, netQuantity, averageEntryPrice);
                    pnl.SetAbsoluteAvgCost(firmId, req.Endclient!, req.Symbol!, netQuantity, averageEntryPrice);
                    subAccountPnl.SetAbsoluteMasterBucketAvgCost(firmId, req.Endclient!, req.Symbol!, netQuantity, averageEntryPrice);
                });

            return Results.Ok(new
            {
                endclient = req.Endclient,
                firmId,
                symbol = req.Symbol,
                netQuantity,
                averageEntryPrice,
            });
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "admin.positions"));
            return Results.Json(
                new { error = "system busy (WAL backpressure)", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// #671/#753 (RFC: admin account reset, PR 3). Handles
    /// <c>POST /api/admin/accounts/{endClientId}/reset</c> — an
    /// operator-driven ATOMIC whole end-client account reset. Mirrors
    /// <see cref="HandleCashLedger"/>'s structural conventions
    /// (audit-first ordering, WAL-backpressure 503, FirmId always
    /// derived from the JWT firm claim rather than the request body,
    /// <see cref="EventDispatcher.DispatchWithPreApply{TEvent}"/> for the
    /// atomic-with-guard-recheck mutation) but is a SINGLE
    /// <see cref="AccountResetEvent"/> rather than a per-field
    /// event — splitting cash/position resets into separate WAL
    /// records would risk a crash exposing a half-reset account.
    ///
    /// <para>
    /// <b>Fail-closed guard.</b> Refuses with 409 while the account
    /// has any working order (<see cref="WorkingOrderBook.CountNonTerminalForOwnerAndFirmIncludingStale"/>,
    /// which excludes terminal orders but — code-review addendum #1 —
    /// deliberately INCLUDES stale orders: a stale order's true venue-
    /// side disposition can no longer be positively confirmed, so
    /// reset must fail closed on it exactly like any other working
    /// order rather than silently discard the possibility the venue
    /// still considers it live) OR any outbound mutation that is
    /// non-terminal or flagged <c>RequiresReconciliation</c>
    /// (<see cref="OutboundMutationLedger.HasNonTerminalMutationForEndClientRef(string, System.Collections.Generic.IReadOnlyCollection{string})"/>,
    /// checked against every stable-reference candidate from
    /// <see cref="IOutboundCommandProtector.CreateStableEndClientRefCandidates"/>
    /// so a stable-reference key rotation cannot hide a pending
    /// mutation recorded under a retired key). NEVER auto-cancels or
    /// auto-resolves either condition — the operator must clear them
    /// first. The guard runs twice: once as a cheap pre-check outside
    /// the dispatcher lock (fast-fail the common case — it only
    /// inspects orders/outbound-mutation state, never positions, so it
    /// carries no resolve-time TOCTOU risk of its own), and again,
    /// AUTHORITATIVELY, inside the event-factory callback — the same
    /// critical region <see cref="EventDispatcher"/> uses to serialise
    /// order submission — so a concurrent submit cannot race this
    /// reset (RFC #753's TOCTOU requirement).
    /// </para>
    ///
    /// <para>
    /// <b>Code-review addendum #3 — live payload resolution.</b> The
    /// absolute reset payload (<see cref="AccountResetPayloadResolver.Resolve"/>)
    /// is resolved from <see cref="PositionKeeper.ForEndClientAndFirm"/>
    /// INSIDE the same event-factory callback as the authoritative
    /// guard re-check — never from a value captured before the
    /// dispatcher lock is acquired. Resolving it earlier would leave a
    /// TOCTOU window in the PAYLOAD ITSELF: a fill/fee/adjustment
    /// landing between the cheap pre-check and lock acquisition could
    /// mutate or introduce a symbol that never makes it into the
    /// persisted <see cref="AccountResetEvent"/>. This uses the
    /// generic <see cref="EventDispatcher.DispatchWithPreApply{TEvent}"/>
    /// overload, whose factory both re-validates the guard and returns
    /// the event to persist together with its apply action, all
    /// resolved at the exact linearization point of the WAL append.
    /// The audit-first entry below therefore records only
    /// (endclient, firmId) — the resolved cash/position payload is
    /// authoritative only in the persisted event and the 200 response.
    /// </para>
    ///
    /// <para>
    /// <b>Rollback (code-review final finding).</b> The generic
    /// <see cref="EventDispatcher.DispatchWithPreApply{TEvent}"/>
    /// factory used here (<c>resolveAndPreApply</c>) is READ-ONLY — it
    /// only resolves the payload and builds the <c>Apply</c>
    /// delegate, never mutating a keeper itself. All in-memory
    /// mutation is deferred to <c>Apply</c>, which the dispatcher only
    /// invokes AFTER a successful, durable Append. Consequently a WAL
    /// Append failure (503, WAL backpressure) needs — and gets — NO
    /// rollback at all: nothing was mutated yet, so every projection
    /// is left byte-for-byte, logically unchanged (no flat
    /// <see cref="PositionKeeper"/> row materialised for a previously
    /// untracked symbol, no <see cref="CashLedger.BalanceChanged"/>
    /// side effect, no sub-account bucket/row change, no margin
    /// release). The <c>rollbackOnApplyFailure</c> delegate passed
    /// below instead guards ONLY the (expected-unreachable,
    /// defense-in-depth) case where <c>Apply</c> itself throws AFTER
    /// the event is already durably appended — it restores the EXACT
    /// pre-reset value captured from the SAME live read used to
    /// resolve the payload (same instant, same lock): cash,
    /// per-symbol position/avg-cost — including, for a symbol that
    /// had NO tracked row before the reset, removing it back to true
    /// absence via <see cref="PositionKeeper.TryRemove"/> rather than
    /// leaving a spurious flat row — the full sub-account PnL bucket
    /// set, and — code-review addendum #2 — the full named sub-account
    /// POSITION row set. Margin release is deliberately NOT rolled
    /// back even in that path: by construction of the guard having
    /// just passed, any reservation released is already orphaned, so
    /// re-releasing it on a retried reset is idempotent-safe.
    /// </para>
    ///
    /// <para>
    /// <b>Code-review addendum #2 — named sub-account positions.</b>
    /// A whole-account reset clears every named
    /// <see cref="SubAccountPositionKeeper"/> row (all sub-accounts,
    /// all symbols) for the account, alongside every named
    /// <see cref="SubAccountPnlKeeper"/> bucket — both are equally
    /// risk-visible state (e.g. the per-sub-account breakdown in
    /// <c>GET /api/positions</c>) that would otherwise reference a
    /// position no longer consistent with the reset aggregate. Named
    /// rows/buckets are only ever CLEARED here, never fabricated —
    /// reset seeding, like position adjustment, only ever targets the
    /// master bucket.
    /// </para>
    /// </summary>
    private static IResult HandleAccountReset(
        string endClientId,
        HttpContext ctx,
        PositionKeeper positions,
        PnlKeeper pnl,
        SubAccountPnlKeeper subAccountPnl,
        SubAccountPositionKeeper subAccountPositions,
        CashKeeper cashKeeper,
        CashLedger cashLedger,
        WorkingOrderBook orders,
        OutboundMutationLedger outboundLedger,
        IOutboundCommandProtector commandProtector,
        IMarginProvider marginProvider,
        CashSeedOptions cashSeeds,
        PositionSeedOptions positionSeeds,
        EventDispatcher dispatcher,
        IAuditLogger audit)
    {
        if (string.IsNullOrWhiteSpace(endClientId))
            return Results.BadRequest(new { error = "endClientId required" });

        var operatorId = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        // RFC #753: firm scope comes EXCLUSIVELY from the caller's JWT
        // firm claim — there is no request-body firm override. Fail
        // closed (401) when the claim is missing or blank, mirroring
        // HandlePositionAdjustment's rationale: silently defaulting a
        // durable, tenant-scoped WRITE to a shared "default" bucket on
        // a malformed/forged token would be a cross-tenant data-
        // integrity issue, not a benign read fallback.
        var firmIdClaim = ctx.User.FindFirstValue(JwtIssuer.FirmClaim);
        if (string.IsNullOrWhiteSpace(firmIdClaim))
        {
            return Results.Json(
                new { error = "firm claim missing or blank on caller JWT" },
                statusCode: StatusCodes.Status401Unauthorized);
        }
        var firmId = firmIdClaim;
        var owner = new EndClientId(endClientId);

        var refCandidates = commandProtector.CreateStableEndClientRefCandidates(firmId, endClientId);

        // Cheap pre-check outside the dispatcher lock — fast-fails the
        // overwhelmingly common "already blocked" case without paying
        // for a lock acquisition. The authoritative re-check — AND the
        // live payload resolution (code-review addendum #3) — both
        // happen inside the dispatcher's critical region below.
        if (IsResetBlocked(orders, outboundLedger, firmId, owner, refCandidates, out var preCheckReason))
            return AccountResetBlockedResult(preCheckReason!);

        // Mutable captures set inside the event-factory callback below
        // (under the dispatcher lock) and read either by the
        // apply-failure rollback delegate (same lock, only if Apply
        // itself throws AFTER a successful Append — see the Rollback
        // remarks above; an Append failure needs no rollback at all)
        // or by this method after DispatchWithPreApply returns.
        // Declared here so both lambdas close over the same locals.
        // WasPresent distinguishes "previously absent" (must be
        // restored via TryRemove, not a flat SetAbsolute(0, 0m), or a
        // spurious row would be materialised) from "previously
        // present with these values".
        Dictionary<string, (bool WasPresent, long NetQuantity, decimal AverageEntryPrice)>? beforePositionsBySymbol = null;
        Dictionary<string, PnlKeeper.PnlSymbolBasisSnapshot>? beforeAvgCostBySymbol = null;
        var beforeCashKeeper = 0m;
        var beforeCashLedger = 0m;
        IReadOnlyList<SubAccountPnlBucketEntry>? beforeBuckets = null;
        IReadOnlyList<SubAccountPositionEntry>? beforeSubAccountPositions = null;
        AccountResetPayload? appliedPayload = null;
        string? invariantViolation = null;

        try
        {
            // Audit-first ordering (Pass-1 review #322 P1.2 pattern):
            // emit the operator's reset intent BEFORE the dispatch so
            // a WAL-backpressured audit append refuses the mutation
            // with 503 rather than committing it un-audited. See the
            // code-review addendum #3 remarks above for why this
            // deliberately omits a resolved cashAvailable/symbolCount
            // preview — that value only ever exists, authoritatively,
            // inside the dispatcher lock.
            EmitAdminConfigChange(audit, ctx, "/api/admin/accounts/reset", AuditOutcomes.Success, new()
            {
                ["endclient"] = endClientId,
                ["firmId"] = firmId,
            }, failClosed: true);

            var outcome = dispatcher.DispatchWithPreApply<AccountResetEvent>(
                resolveAndPreApply: () =>
                {
                    // Authoritative re-check INSIDE the same critical
                    // region EventDispatcher uses to serialise order
                    // submit/cancel/replace dispatch — this closes the
                    // TOCTOU window a check-then-act outside the lock
                    // would leave open.
                    if (IsResetBlocked(orders, outboundLedger, firmId, owner, refCandidates, out _))
                        return ((AccountResetEvent?)null, (Action)(static () => { }));

                    // Code-review addendum #3: resolve the payload
                    // from LIVE PositionKeeper state HERE, at the same
                    // linearization point as the guard re-check above
                    // — never from a value captured before the lock.
                    // An intervening fill/fee/adjustment landing
                    // between the cheap pre-check and this instant is
                    // necessarily reflected in currentPositions, so it
                    // cannot escape the persisted event.
                    var currentPositions = positions.ForEndClientAndFirm(firmId, owner);
                    var payload = AccountResetPayloadResolver.Resolve(
                        firmId, owner, currentPositions, cashSeeds, positionSeeds);

                    // Defense-in-depth invariant re-check.
                    // PositionKeeper.SetAbsolute / PnlKeeper.SetAbsoluteAvgCost
                    // re-check this invariant too, but this method has
                    // no separate post-append apply step to fail out
                    // of cleanly — the resolver's construction rules
                    // make this unreachable in practice, but it is
                    // checked here anyway so a regression fails the
                    // request with 400 (via invariantViolation, since
                    // a lock-scoped factory cannot return an IResult
                    // directly) rather than corrupting state.
                    foreach (var entry in payload.Positions)
                    {
                        if (entry.NetQuantity == 0 && entry.AverageEntryPrice != 0m)
                        {
                            invariantViolation = $"resolved averageEntryPrice must be 0 for flat symbol '{entry.Symbol}'";
                            return ((AccountResetEvent?)null, (Action)(static () => { }));
                        }
                        if (entry.NetQuantity != 0 && entry.AverageEntryPrice <= 0m)
                        {
                            invariantViolation = $"resolved averageEntryPrice must be > 0 for non-flat symbol '{entry.Symbol}'";
                            return ((AccountResetEvent?)null, (Action)(static () => { }));
                        }
                    }

                    var evt = new AccountResetEvent
                    {
                        EndClientId = endClientId,
                        FirmId = firmId,
                        CashAvailable = payload.CashAvailable,
                        Positions = payload.Positions,
                        OperatorId = operatorId,
                    };

                    // "Before" capture for exact apply-failure rollback
                    // (absolute overwrites are not deltas — see
                    // class-level doc). Captured from the SAME live
                    // read as the payload above, so a restore always
                    // targets the TRUE pre-mutation state even if an
                    // intervening mutation landed between the cheap
                    // pre-check and this instant. WasPresent is
                    // recorded per symbol so the rollback can
                    // distinguish "restore to these exact values" from
                    // "this symbol never had a row — remove it back to
                    // true absence" (see PositionKeeper.TryRemove).
                    var currentBySymbol = currentPositions.ToDictionary(static p => p.Symbol, StringComparer.Ordinal);
                    beforePositionsBySymbol = new Dictionary<string, (bool WasPresent, long NetQuantity, decimal AverageEntryPrice)>(StringComparer.Ordinal);
                    beforeAvgCostBySymbol = new Dictionary<string, PnlKeeper.PnlSymbolBasisSnapshot>(StringComparer.Ordinal);
                    foreach (var entry in payload.Positions)
                    {
                        beforePositionsBySymbol[entry.Symbol] = currentBySymbol.TryGetValue(entry.Symbol, out var cur)
                            ? (true, cur.NetQuantity, cur.AverageEntryPrice)
                            : (false, 0L, 0m);
                        // Discriminated capture (known basis / unknown-basis
                        // qty / true absence) — NOT pnl.GetAvgCost, which
                        // collapses the latter two into the same `null`
                        // and would otherwise let a rollback silently wipe
                        // a legacy unknown-basis leg (see PnlKeeper's
                        // CaptureSymbolBasis remarks).
                        beforeAvgCostBySymbol[entry.Symbol] = pnl.CaptureSymbolBasis(firmId, endClientId, entry.Symbol);
                    }
                    beforeCashKeeper = cashKeeper.GetAvailable(firmId, owner);
                    beforeCashLedger = cashLedger.GetAvailable(firmId, owner);
                    beforeBuckets = subAccountPnl.SnapshotBucketsForAccount(firmId, endClientId);
                    beforeSubAccountPositions = subAccountPositions.SnapshotForAccount(firmId, owner);
                    appliedPayload = payload;

                    void Apply()
                    {
                        marginProvider.ReleaseAllReservationsForAccount(firmId, owner);

                        // Named sub-account buckets AND position rows:
                        // CLEARED, never fabricated (code-review
                        // addendum #2). A whole-account reset changes
                        // the aggregate position outright, so any
                        // named bucket/row would otherwise reference a
                        // position that no longer exists — stale risk
                        // state. Historical realized P&L stays
                        // untouched (permanent audit history, not a
                        // basis).
                        subAccountPnl.ClearAllBucketsForAccount(firmId, endClientId);
                        subAccountPositions.ClearAllForAccount(firmId, owner);

                        foreach (var entry in payload.Positions)
                        {
                            positions.SetAbsolute(
                                firmId, owner, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
                            pnl.SetAbsoluteAvgCost(
                                firmId, endClientId, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
                            subAccountPnl.SetAbsoluteMasterBucketAvgCost(
                                firmId, endClientId, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
                        }

                        cashKeeper.SetAbsolute(firmId, owner, payload.CashAvailable);
                        cashLedger.SetAbsolute(firmId, owner, payload.CashAvailable);
                    }

                    return (evt, Apply);
                },
                rollbackOnApplyFailure: () =>
                {
                    // Only reachable if Apply() itself throws AFTER a
                    // successful, durable Append (see the Rollback
                    // remarks above) — an Append failure never invokes
                    // this delegate. Restores exact presence/absence,
                    // not just zeroed values: a symbol with no row
                    // before the reset is put back to true absence via
                    // TryRemove rather than left as a spurious flat
                    // (0, 0m) row that SetAbsolute would otherwise
                    // materialise.
                    foreach (var (symbol, before) in beforePositionsBySymbol!)
                    {
                        if (before.WasPresent)
                            positions.SetAbsolute(firmId, owner, symbol, before.NetQuantity, before.AverageEntryPrice);
                        else
                            positions.TryRemove(firmId, owner, symbol);
                    }
                    foreach (var (symbol, before) in beforeAvgCostBySymbol!)
                    {
                        // RestoreSymbolBasis, not SetAbsoluteAvgCost:
                        // the latter always clears the unknown-basis
                        // leg, which would silently destroy a legacy
                        // unknown-basis quantity that existed before
                        // this reset instead of restoring it.
                        pnl.RestoreSymbolBasis(firmId, endClientId, symbol, before);
                    }
                    subAccountPnl.RestoreBucketsForAccount(firmId, endClientId, beforeBuckets!);
                    subAccountPositions.RestoreForAccount(firmId, owner, beforeSubAccountPositions!);
                    cashKeeper.SetAbsolute(firmId, owner, beforeCashKeeper);
                    cashLedger.SetAbsolute(firmId, owner, beforeCashLedger);
                });

            if (!outcome.Applied)
            {
                return invariantViolation is not null
                    ? Results.BadRequest(new { error = invariantViolation })
                    : AccountResetBlockedResult("account_reset_blocked");
            }

            return Results.Ok(new
            {
                endclient = endClientId,
                firmId,
                cashAvailable = appliedPayload!.CashAvailable,
                positions = appliedPayload.Positions,
            });
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "admin.accounts.reset"));
            return Results.Json(
                new { error = "system busy (WAL backpressure)", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// #671/#753. Shared guard body evaluated both as a cheap pre-
    /// check (outside the dispatcher lock) and as the authoritative
    /// re-check (inside the event-factory callback, under the lock)
    /// by <see cref="HandleAccountReset"/>. Never mutates anything —
    /// callers are responsible for never auto-cancelling or auto-
    /// resolving either condition (RFC #753: fail closed, operator
    /// clears manually).
    /// </summary>
    private static bool IsResetBlocked(
        WorkingOrderBook orders,
        OutboundMutationLedger outboundLedger,
        string firmId,
        EndClientId owner,
        IReadOnlyCollection<string> refCandidates,
        out string? reason)
    {
        // Code-review addendum #1: reset-specific, STALE-INCLUSIVE
        // count. Unlike the max-open-orders risk-budget check (which
        // deliberately exempts stale ghosts so a venue desync cannot
        // freeze new trading), reset must fail closed on a stale order
        // — its true venue-side disposition can no longer be
        // positively confirmed, so the operator must resolve it
        // (cancel / clear-stale) before an irreversible reset proceeds.
        if (orders.CountNonTerminalForOwnerAndFirmIncludingStale(firmId, owner) > 0)
        {
            reason = "open_working_order";
            return true;
        }
        if (outboundLedger.HasNonTerminalMutationForEndClientRef(firmId, refCandidates))
        {
            reason = "non_terminal_outbound_mutation";
            return true;
        }
        reason = null;
        return false;
    }

    private static IResult AccountResetBlockedResult(string reason) =>
        Results.Json(
            new { error = "account_reset_blocked", reason },
            statusCode: StatusCodes.Status409Conflict);

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
            EmitAdminConfigChange(audit, ctx, "/api/admin/kill", AuditOutcomes.Success, killDetails, failClosed: true);
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
            EmitAdminConfigChange(audit, ctx, "/api/admin/halts", AuditOutcomes.Success, new()
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
            EmitAdminConfigChange(audit, ctx, "/api/admin/session-phase", AuditOutcomes.Success, new()
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
/// Body for <c>POST /api/admin/session-phase[/{symbol}|/default]</c> (#108).
/// </summary>
public sealed class SessionPhasePayload
{
    public string? Phase { get; set; }
}

/// <summary>
/// Body for <c>POST /api/admin/firms/{firmId}/orders/{clOrdId}/mark-stale</c> (#132 slice 1).
/// </summary>
public sealed record MarkStaleRequest(string? Reason);

/// <summary>
/// Body for <c>POST /api/admin/cash</c> (Q2.2 / #269). Operator-driven
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

/// <summary>
/// Body for <c>POST /api/admin/positions</c> (#671/#753 RFC, PR 1).
/// Operator-driven ABSOLUTE position overwrite. <see cref="NetQuantity"/>
/// is signed (positive = long, negative = short); <see cref="AverageEntryPrice"/>
/// must be exactly 0 when <see cref="NetQuantity"/> is 0, and strictly
/// positive otherwise (RFC #753 invariant, enforced in
/// <c>AdminEndpoints.HandlePositionAdjustment</c> and, defense-in-depth,
/// in <c>PositionKeeper.SetAbsolute</c>). There is deliberately no
/// <c>FirmId</c> field here — per the RFC's "admin operations are
/// scoped to the administrator's JWT firm" product decision, the firm
/// is always derived from the caller's JWT firm claim, never accepted
/// from the request body. <see cref="Reference"/> is operator free-form
/// (ticket id, journal note), mirroring <see cref="CashLedgerRequest.Reference"/>.
///
/// <para>
/// Code-review addendum (#671/#753 PR 1). <see cref="NetQuantity"/> and
/// <see cref="AverageEntryPrice"/> are nullable at the JSON-binding
/// level ON PURPOSE: both fields are semantically REQUIRED (there is
/// no sensible default for an absolute overwrite), but the intentional
/// "flatten to zero" request legitimately sends a literal
/// <c>netQuantity: 0, averageEntryPrice: 0</c>. Using non-nullable
/// <c>long</c>/<c>decimal</c> would make an omitted field
/// indistinguishable from an explicit <c>0</c> (System.Text.Json
/// silently defaults missing value-typed properties), silently
/// accepting a malformed/incomplete request as a flatten-to-zero
/// instruction. <c>HandlePositionAdjustment</c> rejects either field
/// being <c>null</c> with 400 before evaluating the flatten/invariant
/// rule below.
/// </para>
/// </summary>
public sealed class PositionAdjustmentRequest
{
    public string? Endclient { get; set; }
    public string? Symbol { get; set; }
    public long? NetQuantity { get; set; }
    public decimal? AverageEntryPrice { get; set; }
    public string? Reference { get; set; }
}
