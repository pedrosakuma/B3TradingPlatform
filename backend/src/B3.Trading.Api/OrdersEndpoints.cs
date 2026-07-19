using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
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
            RestOrderIdempotencyStore idempotency,
            OutboundMutationLedger outboundLedger,
            WorkingOrderBook book,
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

            SubAccountId? subAccount = null;
            if (!string.IsNullOrWhiteSpace(req.SubAccountId))
            {
                try { subAccount = new SubAccountId(req.SubAccountId); }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = $"invalid subAccountId: {ex.Message}" });
                }
            }

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            var idempotencyValues = ctx.Request.Headers["Idempotency-Key"];
            if (idempotencyValues.Count > 1)
                return Results.BadRequest(new { error = "multiple Idempotency-Key values are not allowed" });
            var idempotencyKey = idempotencyValues.ToString();
            RestOrderIdempotencyIdentity? idempotencyIdentity = null;
            string? requestHash = null;
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                idempotencyIdentity = new RestOrderIdempotencyIdentity(
                    firm,
                    owner.Value,
                    ResolvePrincipal(ctx),
                    "POST /orders",
                    idempotencyKey);
                requestHash = CanonicalRequestHash(
                    req,
                    side,
                    type,
                    tif,
                    displayPolicy,
                    subAccount);
                try
                {
                    var resolution = await idempotency.ResolveAsync(
                        idempotencyIdentity,
                        requestHash,
                        ct);
                    if (resolution.Kind == RestOrderIdempotencyResolutionKind.Conflict)
                        return Results.Conflict(new { error = "idempotency_key_reused_with_different_request" });
                    if (resolution.Kind == RestOrderIdempotencyResolutionKind.Replayed)
                        return MapReplayedSubmission(resolution.Binding!, outboundLedger, book);
                }
                catch (RestOrderIdempotencyUnavailableException)
                {
                    return IdempotencyUnavailable();
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            // Mutable directory/activation checks intentionally run only after
            // an existing scoped idempotency binding has had a chance to replay.
            if (subAccount is not null)
            {
                if (!subAccounts.TryGet(firm, subAccount.Value, out var entry))
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

            var securityId = req.SecurityId;
            if (securityId == 0 && symbols.TryResolve(req.Symbol, out var resolved))
                securityId = resolved;
            var submission = new OrderSubmissionRequest(
                owner, firm, req.Symbol, securityId, side, type,
                req.Quantity, req.Price, OrderSubmissionSource.Manual,
                TimeInForce: tif,
                StopPrice: req.StopPrice,
                GoodTillDate: req.GoodTillDate,
                DisplayQty: req.DisplayQty,
                DisplayResetPolicy: displayPolicy,
                SubAccountId: subAccount,
                MinQty: req.MinQty)
            {
                UseDurableOutboundCoordinator = true,
            };

            if (idempotencyIdentity is null)
            {
                ctx.Response.Headers["Idempotency-Key-Required"] = "true";
                ctx.Response.Headers.Append(
                    "Warning",
                    "299 B3TradingPlatform \"Idempotency-Key will become required for POST /orders\"");
                MetricsRegistry.OrdersMissingIdempotencyKey.Add(
                    1,
                    new KeyValuePair<string, object?>("firmId", firm),
                    new KeyValuePair<string, object?>("endpoint", "POST /orders"));
                var unkeyedResult = await submitter.SubmitAsync(submission, ct);
                return MapSubmissionResult(
                    unkeyedResult,
                    replayed: false,
                    ResolveState(unkeyedResult.MutationId, unkeyedResult.ClOrdId, outboundLedger, book));
            }

            RestOrderIdempotencyExecution<OrderSubmissionResult> execution;
            try
            {
                execution = await idempotency.ExecuteAsync(
                    idempotencyIdentity,
                    requestHash!,
                    async idempotencyContext =>
                        await submitter.SubmitAsync(
                            submission with { IdempotencyContext = idempotencyContext },
                            ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (RestOrderIdempotencyUnavailableException)
            {
                return IdempotencyUnavailable();
            }

            if (execution.Kind == RestOrderIdempotencyExecutionKind.Conflict)
                return Results.Conflict(new { error = "idempotency_key_reused_with_different_request" });
            if (execution.Kind == RestOrderIdempotencyExecutionKind.Replayed)
            {
                var binding = execution.Binding!;
                return MapReplayedSubmission(binding, outboundLedger, book);
            }

            var result = execution.Value!;
            return MapSubmissionResult(
                result,
                replayed: false,
                ResolveState(result.MutationId, result.ClOrdId, outboundLedger, book));
        });

        group.MapGet("/mutations/{mutationId:guid}", (
            Guid mutationId,
            HttpContext ctx,
            EndClientRegistry registry,
            RestOrderIdempotencyStore idempotency,
            OutboundMutationLedger outboundLedger,
            IOutboundCommandProtector protector,
            WorkingOrderBook book) =>
        {
            var id = new OutboundMutationId(mutationId);
            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            if (idempotency.TryGetByMutation(id, out var binding) && binding is not null)
            {
                try
                {
                    if (!idempotency.IsOwnedBy(
                            binding,
                            firm,
                            owner.Value,
                            ResolvePrincipal(ctx),
                            "POST /orders"))
                        return Results.NotFound();
                }
                catch (RestOrderIdempotencyUnavailableException)
                {
                    return IdempotencyUnavailable();
                }
                return Results.Ok(MutationResponse(
                    binding.MutationId,
                    binding.ClOrdId,
                    ResolveState(binding.MutationId, binding.ClOrdId, outboundLedger, book),
                    replayed: true));
            }
            if (!outboundLedger.TryGet(id, out var mutation)
                || mutation is null
                || mutation.Kind != OutboundMutationKind.New
                || !string.Equals(mutation.FirmId, firm, StringComparison.Ordinal)
                || !string.Equals(
                    mutation.EndClientRef,
                    protector.CreateStableEndClientRef(firm, owner.Value),
                    StringComparison.Ordinal))
                return Results.NotFound();
            return Results.Ok(MutationResponse(
                mutation.MutationId,
                mutation.PrimaryClOrdId,
                mutation.State.ToString(),
                replayed: false));
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
                OrderModifyResultKind.GatewayAmbiguous =>
                    Results.Json(
                        new { error = "gateway send outcome ambiguous", clOrdId = result.NewClOrdId.ToString() },
                        statusCode: StatusCodes.Status502BadGateway),
                OrderModifyResultKind.ReconciliationRequired =>
                    Results.Json(
                        new
                        {
                            error = "replace resolution requires reconciliation",
                            detail = result.Reason,
                            clOrdId = result.NewClOrdId.ToString(),
                        },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
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
                OrderCancelResultKind.Conflict =>
                    Results.Conflict(new { error = result.Reason }),
                OrderCancelResultKind.WalBackpressure =>
                    Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = result.Reason },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderCancelResultKind.GatewayFailed =>
                    Results.Json(
                        new { error = "gateway unavailable", clOrdId = result.CancelClOrdId.ToString() },
                        statusCode: StatusCodes.Status502BadGateway),
                OrderCancelResultKind.ReconciliationRequired =>
                    Results.Json(
                        new
                        {
                            error = "cancel resolution requires reconciliation",
                            detail = result.Reason,
                            clOrdId = result.CancelClOrdId == 0
                                ? null
                                : result.CancelClOrdId.ToString(),
                        },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
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

    private static string ResolvePrincipal(HttpContext ctx) =>
        ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
        ?? throw new InvalidOperationException("Authenticated request missing sub claim.");

    private static IResult MapReplayedSubmission(
        RestOrderIdempotencyBindingSnapshot binding,
        OutboundMutationLedger ledger,
        WorkingOrderBook book)
    {
        var state = ResolveState(binding.MutationId, binding.ClOrdId, ledger, book);
        var response = MutationResponse(
            binding.MutationId,
            binding.ClOrdId,
            state,
            replayed: true,
            error: state switch
            {
                nameof(OutboundMutationState.ProvenUnsent) => "gateway unavailable",
                nameof(OutboundMutationState.Ambiguous) => "outbound mutation requires reconciliation",
                nameof(OutboundMutationState.AttemptIntentPrepared) => "outbound mutation outcome is unknown",
                nameof(OutboundMutationState.FramePrepared) => "outbound mutation outcome is unknown",
                "RecordedPendingApproval" => "outbound approval is not durably committed",
                _ => null,
            });
        return state switch
        {
            nameof(OutboundMutationState.ProvenUnsent) =>
                Results.Json(response, statusCode: StatusCodes.Status502BadGateway),
            nameof(OutboundMutationState.Ambiguous)
                or nameof(OutboundMutationState.AttemptIntentPrepared)
                or nameof(OutboundMutationState.FramePrepared)
                or nameof(OutboundMutationState.LegacyUnknown) =>
                Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable),
            "RecordedPendingApproval" =>
                Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Accepted($"/orders/mutations/{binding.MutationId}", response),
        };
    }

    private static IResult MapSubmissionResult(
        OrderSubmissionResult result,
        bool replayed,
        string state)
    {
        var response = MutationResponse(
            result.MutationId,
            result.ClOrdId,
            state,
            replayed,
            result.Reason,
            result.Code,
            status: result.Kind == OrderSubmissionResultKind.Rejected ? "Rejected" : null,
            error: result.Kind switch
            {
                OrderSubmissionResultKind.GatewayFailed => "gateway unavailable",
                OrderSubmissionResultKind.ReconciliationRequired =>
                    "WAL reconciliation required; service draining",
                OrderSubmissionResultKind.WalBackpressure =>
                    result.Code == "wal_faulted"
                        ? "service unavailable (WAL faulted)"
                        : "system busy (WAL backpressure)",
                _ => null,
            });
        return result.Kind switch
        {
            OrderSubmissionResultKind.Accepted or OrderSubmissionResultKind.Rejected =>
                Results.Accepted($"/orders/mutations/{result.MutationId}", response),
            OrderSubmissionResultKind.GatewayFailed =>
                Results.Json(response, statusCode: StatusCodes.Status502BadGateway),
            OrderSubmissionResultKind.WalBackpressure =>
                Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable),
            OrderSubmissionResultKind.Drained =>
                Results.Json(
                    new { error = "service draining" },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            OrderSubmissionResultKind.BadRequest =>
                Results.BadRequest(new { error = result.Reason }),
            OrderSubmissionResultKind.DuplicateClOrdId =>
                Results.Conflict(new { error = result.Reason, clOrdId = result.ClOrdId.ToString() }),
            OrderSubmissionResultKind.ReconciliationRequired =>
                Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static object MutationResponse(
        OutboundMutationId mutationId,
        ulong clOrdId,
        string state,
        bool replayed,
        string? reason = null,
        string? code = null,
        string? status = null,
        string? error = null) => new
        {
            mutationId = mutationId.Value == Guid.Empty ? null : mutationId.ToString(),
            clOrdId = clOrdId == 0 ? null : clOrdId.ToString(),
            state,
            lookupUrl = mutationId.Value == Guid.Empty
                ? null
                : $"/orders/mutations/{mutationId}",
            replayed,
            status = status ?? (state == "RejectedBeforeApproval" ? "Rejected" : null),
            reason,
            code,
            error,
        };

    private static string ResolveState(
        OutboundMutationId mutationId,
        ulong clOrdId,
        OutboundMutationLedger ledger,
        WorkingOrderBook? book)
    {
        if (mutationId.Value != Guid.Empty
            && ledger.TryGet(mutationId, out var mutation)
            && mutation is not null)
            return mutation.State.ToString();
        if (book?.TryGet(clOrdId, out var order) == true && order is not null)
            return order.Status == OrderStatus.Rejected
                ? "RejectedBeforeApproval"
                : "RecordedPendingApproval";
        return "RecordedPendingApproval";
    }

    private static string CanonicalRequestHash(
        SubmitOrderRequest request,
        OrderSide side,
        OrderType type,
        TimeInForce timeInForce,
        DisplayResetPolicy? displayResetPolicy,
        SubAccountId? subAccount)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            symbol = request.Symbol,
            securityId = request.SecurityId,
            side = side.ToString(),
            type = type.ToString(),
            request.Quantity,
            request.Price,
            timeInForce = timeInForce.ToString(),
            request.StopPrice,
            goodTillDate = request.GoodTillDate?.ToUniversalTime(),
            request.DisplayQty,
            displayResetPolicy = request.DisplayQty is null
                ? null
                : (displayResetPolicy ?? DisplayResetPolicy.Always).ToString(),
            subAccountId = subAccount?.Value,
            request.MinQty,
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static IResult IdempotencyUnavailable() =>
        Results.Json(
            new
            {
                error = "idempotency history unavailable; operator reconciliation required",
                code = "idempotency_history_unavailable",
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
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
