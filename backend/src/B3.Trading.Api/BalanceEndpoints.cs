using System.Security.Claims;
using B3.Trading.Api.WebSockets;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class BalanceEndpoints
{
    public static IEndpointRouteBuilder MapBalance(this IEndpointRouteBuilder app)
    {
        app.MapGet("/balance", [Authorize] (HttpContext ctx, EndClientRegistry registry, CashLedger cash) =>
        {
            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
            var owner = registry.Register(sub);
            var firm = ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
            return Results.Ok(new BalanceDto(cash.GetAvailable(firm, owner)));
        });

        return app;
    }

    /// <summary>
    /// #679. Self-service cash deposit for sandbox/demo accounts —
    /// mounted conditionally (see <c>TradingEndpointsExtensions</c>)
    /// only when <see cref="SandboxCashOptions.AllowSelfCashDeposit"/>
    /// is enabled, so the route is absent (404) rather than merely
    /// forbidden (403) in the default/production configuration.
    /// <para>
    /// Reuses the same <see cref="CashLedgerEvent"/> + fold-into-both-
    /// ledgers path as <c>POST /admin/cash</c> (see
    /// <c>AdminEndpoints.HandleCashLedger</c>) — the WAL/replay code
    /// doesn't distinguish operator-driven from self-driven deposits,
    /// only <see cref="CashLedgerEvent.OperatorId"/> differs (set to
    /// the depositor's own sub here instead of an admin's).
    /// </para>
    /// Self-scoped: an authenticated end-client can only ever deposit
    /// into their own balance (there is no target-endclient in the
    /// request body, unlike the admin endpoint).
    /// </summary>
    public static IEndpointRouteBuilder MapBalanceSelfDeposit(this IEndpointRouteBuilder app)
    {
        app.MapPost("/balance/deposit", [Authorize] (
            SelfDepositRequest? req,
            HttpContext ctx,
            EndClientRegistry registry,
            CashKeeper keeper,
            CashLedger cashLedger,
            Microsoft.Extensions.Options.IOptions<SandboxCashOptions> options,
            EventDispatcher dispatcher,
            IAuditLogger audit) =>
        {
            var opts = options.Value;
            if (req is null || req.Amount <= 0m)
                return Results.BadRequest(new { error = "amount must be > 0" });
            if (req.Amount > opts.MaxDepositAmount)
                return Results.UnprocessableEntity(new
                {
                    error = "amount_exceeds_limit",
                    maxDepositAmount = opts.MaxDepositAmount,
                });

            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
            var owner = registry.Register(sub);
            var firmId = ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
            var currency = "BRL"; // v0 whitelist, mirrors /admin/cash.

            var projectedBalance = keeper.GetAvailable(firmId, owner) + req.Amount;
            if (projectedBalance > opts.MaxBalanceAfterDeposit)
                return Results.UnprocessableEntity(new
                {
                    error = "balance_exceeds_limit",
                    maxBalanceAfterDeposit = opts.MaxBalanceAfterDeposit,
                    current = keeper.GetAvailable(firmId, owner),
                });

            try
            {
                // Audit-first ordering (mirrors AdminEndpoints.HandleCashLedger):
                // a WAL-backpressured audit append refuses the deposit with 503
                // rather than committing it un-audited.
                var auditEvt = new AuditLogEvent
                {
                    EventType = AuditEventTypes.SandboxCashSelfDeposit,
                    Outcome = AuditOutcomes.Success,
                    ActorUserId = sub,
                    ActorUsername = sub,
                    ActorFirm = firmId,
                    ActorRole = ctx.User.FindFirstValue(JwtIssuer.RoleClaim),
                    SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
                    ResourcePath = "/balance/deposit",
                    Details = new()
                    {
                        ["amount"] = req.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["currency"] = currency,
                    },
                };
                audit.LogOrFail(auditEvt);

                dispatcher.Dispatch(
                    new CashLedgerEvent
                    {
                        EndClientId = sub,
                        FirmId = firmId,
                        Operation = "Deposit",
                        Amount = req.Amount,
                        Currency = currency,
                        Reference = "self-service",
                        OperatorId = sub,
                    },
                    () =>
                    {
                        keeper.ApplyDeposit(firmId, owner, req.Amount);
                        cashLedger.ApplyDeposit(firmId, owner, req.Amount);
                    });

                return Results.Ok(new
                {
                    amount = req.Amount,
                    currency,
                    available = cashLedger.GetAvailable(firmId, owner),
                });
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "balance.self_deposit"));
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }
}

/// <summary>Body for <c>POST /balance/deposit</c> (#679).</summary>
public sealed class SelfDepositRequest
{
    public decimal Amount { get; set; }
}
