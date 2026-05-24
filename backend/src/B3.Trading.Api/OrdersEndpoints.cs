using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders").RequireAuthorization();

        group.MapGet("/", (HttpContext ctx, WorkingOrderBook book, EndClientRegistry registry) =>
        {
            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            // PR #316 P1. Scope by firm so the same JWT sub registered
            // in two firms doesn't see the other firm's orders.
            var orders = book.ForEndClientAndFirm(firm, owner).Select(o => o.ToDto());
            return Results.Ok(orders);
        });

        group.MapPost("/", async (
            SubmitOrderRequest req,
            HttpContext ctx,
            EndClientRegistry registry,
            OrderSubmissionService submitter,
            SymbolDirectory symbols,
            SubAccountsRegistry subAccounts,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<OrderSide>(req.Side, ignoreCase: true, out var side))
                return Results.BadRequest(new { error = $"invalid side '{req.Side}'" });
            if (!Enum.TryParse<OrderType>(req.Type, ignoreCase: true, out var type))
                return Results.BadRequest(new { error = $"invalid type '{req.Type}'" });
            // Q1.1 (#253). Optional TIF (default Day); reject malformed
            // before allocating a ClOrdID so the bad request never enters
            // the WAL pipeline.
            var tif = TimeInForce.Day;
            if (!string.IsNullOrWhiteSpace(req.TimeInForce)
                && !Enum.TryParse<TimeInForce>(req.TimeInForce, ignoreCase: true, out tif))
                return Results.BadRequest(new { error = $"invalid timeInForce '{req.TimeInForce}'" });

            // Q3.4 (#284). Parse the optional iceberg display-qty reset
            // policy enum the same way as TIF (case-insensitive string)
            // — the host does not register JsonStringEnumConverter so a
            // numeric form would otherwise be required. Reject malformed
            // before the submit pipeline so the bad request never enters
            // the WAL. The DisplayQty risk check (0 < DisplayQty <= Quantity)
            // is enforced by Domain.Order's ctor and surfaces as BadRequest
            // from OrderSubmissionService.
            //
            // Pass-1 review (#297, follow-up #298). The B3.EntryPoint.Client
            // SDK 0.14.3 exposes only MaxFloor on NewOrderRequest — there is
            // no refresh-policy field — so any policy other than Always
            // would silently default to Always at the venue, breaking the
            // Never contract entirely. Reject OnPartialFill / Never at the
            // REST boundary (and again in OrderSubmissionService as a
            // defensive risk check covering non-REST callers) until the SDK
            // exposes the field. The Domain enum + WAL/snapshot fields are
            // intentionally retained so this gate can be lifted with a
            // one-line gateway change later (see #298).
            DisplayResetPolicy? displayPolicy = null;
            if (!string.IsNullOrWhiteSpace(req.DisplayResetPolicy))
            {
                if (!Enum.TryParse<DisplayResetPolicy>(req.DisplayResetPolicy, ignoreCase: true, out var parsedPolicy))
                    return Results.BadRequest(new { error = $"invalid displayResetPolicy '{req.DisplayResetPolicy}'" });
                if (parsedPolicy != DisplayResetPolicy.Always)
                    return Results.BadRequest(new
                    {
                        error =
                            $"displayResetPolicy={parsedPolicy} is not supported by the current entrypoint SDK; " +
                            "supported: Always. Track issue #298.",
                    });
                displayPolicy = parsedPolicy;
            }

            // Q4.1 (#301). Parse the optional sub-account hint, validate
            // the id (1-64 chars, alphanumerics + ._-) via the value-type
            // constructor, and confirm it exists and is active for the
            // caller's firm. Active-status is checked again inside
            // SubAccountLimitsCheck under the dispatcher lock — this
            // is a fast-fail before any WAL write happens.
            //
            // PR review #301 P2: distinguish the two rejection cases
            // with structured reasons so clients and metrics can tell
            // "unknown id" apart from "operator-disabled".
            //   * sub_account_not_registered — never seen this id
            //     for this firm (or it was created under a different
            //     firm and the caller's claim mismatches);
            //   * sub_account_deactivated   — registered but soft-
            //     deleted via DELETE /sub-accounts.
            SubAccountId? subAccount = null;
            if (!string.IsNullOrWhiteSpace(req.SubAccountId))
            {
                try { subAccount = new SubAccountId(req.SubAccountId); }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = $"invalid subAccountId: {ex.Message}" });
                }
                var firmForLookup = ResolveFirm(ctx);
                if (!subAccounts.TryGet(firmForLookup, subAccount.Value, out var entry))
                    return Results.BadRequest(new
                    {
                        error = $"sub-account '{subAccount.Value}' is not registered for firm",
                        reason = "sub_account_not_registered",
                    });
                if (!entry.Active)
                    return Results.BadRequest(new
                    {
                        error = $"sub-account '{subAccount.Value}' has been deactivated for firm",
                        reason = "sub_account_deactivated",
                    });
            }

            // SecurityId resolution: explicit non-zero in the payload
            // wins (preserves the conformance contract). Otherwise look
            // up the directory by symbol — that is the path the trader
            // UI takes, since the ticket form does not expose the
            // numeric SecurityId.
            var securityId = req.SecurityId;
            if (securityId == 0 && symbols.TryResolve(req.Symbol, out var resolved))
                securityId = resolved;

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);

            var result = await submitter.SubmitAsync(new OrderSubmissionRequest(
                owner, firm, req.Symbol, securityId, side, type,
                req.Quantity, req.Price, OrderSubmissionSource.Manual,
                TimeInForce: tif,
                StopPrice: req.StopPrice,
                GoodTillDate: req.GoodTillDate,
                DisplayQty: req.DisplayQty,
                DisplayResetPolicy: displayPolicy,
                SubAccountId: subAccount,
                MinQty: req.MinQty), ct);

            return result.Kind switch
            {
                OrderSubmissionResultKind.Accepted =>
                    Results.Accepted($"/orders/{result.ClOrdId}", new { ClOrdId = result.ClOrdId.ToString() }),
                OrderSubmissionResultKind.Rejected =>
                    Results.Accepted($"/orders/{result.ClOrdId}",
                        new { ClOrdId = result.ClOrdId.ToString(), Status = "Rejected", Reason = result.Reason, Code = result.Code }),
                OrderSubmissionResultKind.GatewayFailed =>
                    Results.Json(
                        new { error = "gateway unavailable", clOrdId = result.ClOrdId.ToString() },
                        statusCode: StatusCodes.Status502BadGateway),
                OrderSubmissionResultKind.WalBackpressure =>
                    Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = result.Reason },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderSubmissionResultKind.Drained =>
                    Results.Json(
                        new { error = "service draining" },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderSubmissionResultKind.BadRequest =>
                    Results.BadRequest(new { error = result.Reason }),
                OrderSubmissionResultKind.DuplicateClOrdId =>
                    Results.Conflict(new { error = result.Reason, clOrdId = result.ClOrdId.ToString() }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        group.MapPut("/{clOrdId}", async (
            string clOrdId,
            ModifyOrderRequest req,
            HttpContext ctx,
            EndClientRegistry registry,
            OrderModifyService modifier,
            CancellationToken ct) =>
        {
            if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                return Results.NotFound();

            // Q1.1 (#253). Optional TIF (null = no change) — parse as
            // string + case-insensitive enum to mirror POST exactly,
            // since the host does not register JsonStringEnumConverter
            // and would otherwise force REST callers to send the
            // numeric enum value over JSON.
            TimeInForce? tif = null;
            if (!string.IsNullOrWhiteSpace(req.TimeInForce))
            {
                if (!Enum.TryParse<TimeInForce>(req.TimeInForce, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"invalid timeInForce '{req.TimeInForce}'" });
                tif = parsed;
            }

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            var result = await modifier.ModifyAsync(
                new OrderModifyRequest(
                    owner, clOrdIdU, req.Quantity, req.Price,
                    tif, req.StopPrice, req.GoodTillDate,
                    FirmId: firm),
                ct);

            return result.Kind switch
            {
                OrderModifyResultKind.Accepted =>
                    Results.Accepted(
                        $"/orders/{result.NewClOrdId}",
                        new { ClOrdId = result.NewClOrdId.ToString(), OriginalClOrdId = clOrdIdU.ToString() }),
                OrderModifyResultKind.NotFound =>
                    Results.NotFound(),
                OrderModifyResultKind.Conflict =>
                    Results.Conflict(new { error = result.Reason }),
                OrderModifyResultKind.BadRequest =>
                    Results.BadRequest(new { error = result.Reason }),
                OrderModifyResultKind.RiskRejected =>
                    Results.UnprocessableEntity(new { error = result.Reason, code = result.Code }),
                OrderModifyResultKind.GatewayFailed =>
                    Results.Json(
                        new { error = "gateway unavailable", clOrdId = result.NewClOrdId.ToString() },
                        statusCode: StatusCodes.Status502BadGateway),
                OrderModifyResultKind.WalBackpressure =>
                    Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = result.Reason },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderModifyResultKind.Drained =>
                    Results.Json(
                        new { error = "service draining" },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderModifyResultKind.DuplicateClOrdId =>
                    Results.Conflict(new { error = result.Reason, newClOrdId = result.NewClOrdId.ToString() }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        group.MapDelete("/{clOrdId}", async (
            string clOrdId,
            HttpContext ctx,
            EndClientRegistry registry,
            OrderCancelService canceller,
            CancellationToken ct) =>
        {
            if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                return Results.NotFound();

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            // Sub-issue #171 (E): REST cancels now go through the WAL-
            // durable OrderCancelRequestedEvent path (RFC §4.6 / §4.8).
            // botOrigin is null — REST is not a bot session.
            // PR #316 P1 — pass firm so the service rejects (as
            // NotFound) cancels that target an order owned by a
            // different firm, matching GET /orders scoping.
            var result = await canceller.CancelAsync(owner, clOrdIdU, ct, firmId: firm);
            return result.Kind switch
            {
                OrderCancelResultKind.Accepted => Results.NoContent(),
                OrderCancelResultKind.NotFound => Results.NotFound(),
                OrderCancelResultKind.Stale =>
                    Results.Conflict(new { error = "order is marked stale", reason = result.Reason }),
                OrderCancelResultKind.WalBackpressure =>
                    Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = result.Reason },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderCancelResultKind.GatewayFailed =>
                    Results.Json(
                        new { error = "gateway unavailable", clOrdId = result.CancelClOrdId.ToString() },
                        statusCode: StatusCodes.Status502BadGateway),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        return app;
    }

    private static EndClientId ResolveOwner(HttpContext ctx, EndClientRegistry registry)
    {
        var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
        return registry.Register(sub);
    }

    private static string ResolveFirm(HttpContext ctx) =>
        ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
}

public sealed record SubmitOrderRequest(
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    decimal? Price,
    /// <summary>Q1.1 (#253). Optional; defaults to <c>"Day"</c>.</summary>
    string? TimeInForce = null,
    /// <summary>Q1.1 (#253). Required for <c>StopLoss</c>/<c>StopLimit</c>.</summary>
    decimal? StopPrice = null,
    /// <summary>Q1.1 (#253). Required when <c>TimeInForce == "GTD"</c>.</summary>
    DateTimeOffset? GoodTillDate = null,
    /// <summary>Q3.4 (#284). Native iceberg / reserve display quantity. Null
    /// = full disclosure. Validated as <c>0 &lt; DisplayQty &lt;= Quantity</c>
    /// by the submit pipeline.</summary>
    long? DisplayQty = null,
    /// <summary>Q3.4 (#284). Refresh policy for the visible portion of an
    /// iceberg order. Accepts case-insensitive <see cref="Domain.DisplayResetPolicy"/>
    /// name (<c>"Always" | "OnPartialFill" | "Never"</c>). Defaults to <c>Always</c>
    /// when <c>DisplayQty</c> is set and this field is null. Must be null when
    /// <c>DisplayQty</c> is null.</summary>
    string? DisplayResetPolicy = null,
    /// <summary>Q4.1 (#301). Optional sub-account bucket the order is
    /// booked against. Must satisfy <see cref="B3.Trading.Domain.SubAccountId"/>
    /// validation (1-64 chars, alphanumerics + <c>._-</c>); rejected with
    /// HTTP 400 if invalid (<c>reason: "sub_account_not_registered"</c>
    /// when unknown, <c>reason: "sub_account_deactivated"</c> when
    /// soft-deleted). <c>null</c> retains pre-#301 master-only
    /// behaviour.</summary>
    string? SubAccountId = null,
    /// <summary>#457. Optional minimum execution quantity (FIX MinQty).
    /// When set, the venue must fill at least this many contracts at
    /// submit time or reject the order. Validated as
    /// <c>0 &lt; MinQty &lt;= Quantity</c> by the submit pipeline
    /// (<see cref="B3.Trading.Domain.Order"/>'s constructor).</summary>
    long? MinQty = null);

public sealed record ModifyOrderRequest(
    long Quantity,
    decimal? Price,
    /// <summary>Q1.1 (#253). Optional override; null = keep original. Accepts
    /// the case-insensitive <see cref="TimeInForce"/> name (e.g. <c>"GTD"</c>)
    /// — mirrors the POST submit contract since the host does not register
    /// <c>JsonStringEnumConverter</c>.</summary>
    string? TimeInForce = null,
    /// <summary>Q1.1 (#253). Optional override; null = keep original. Required when modifying into <c>StopLoss</c>/<c>StopLimit</c> — but OrderType is not modifiable, so in practice only meaningful for orders that already are stop orders.</summary>
    decimal? StopPrice = null,
    /// <summary>Q1.1 (#253). Optional override; null = keep original (or auto-cleared when TIF is moved away from <c>GTD</c>). Required when changing TIF to <c>GTD</c>.</summary>
    DateTimeOffset? GoodTillDate = null);

