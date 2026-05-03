using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// HTTP surface for the algo orders v0 RFC §4.9. Handles the parent
/// lifecycle only — actual child slice generation is the engine's job
/// (slices 4+). v0 endpoints accept and durably record the user's
/// intent; the parent stays in <c>PendingNew</c>/<c>Cancelling</c>
/// indefinitely until a future engine slice promotes it.
/// </summary>
public static class AlgoEndpoints
{
    public static IEndpointRouteBuilder MapAlgo(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/algo").RequireAuthorization();

        group.MapGet("/", (HttpContext ctx, AlgoBook algos, EndClientRegistry registry) =>
        {
            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            var list = algos.EnumerateForOwner(firm, owner, includeTerminal: false)
                .Select(a => a.ToDto());
            return Results.Ok(list);
        });

        group.MapGet("/{algoId}", (string algoId, HttpContext ctx, AlgoBook algos, EndClientRegistry registry) =>
        {
            if (!ulong.TryParse(algoId, out var id) || id == 0)
                return Results.NotFound();
            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            if (!algos.TryGet(firm, id, out var algo) || algo is null || algo.Owner != owner)
                return Results.NotFound();
            return Results.Ok(algo.ToDto());
        });

        group.MapPost("/", (
            CreateAlgoRequest req,
            HttpContext ctx,
            EndClientRegistry registry,
            AlgoIdRegistry algoIds,
            AlgoBook algos,
            IAlgoEventSink sink,
            EventDispatcher dispatcher,
            DrainState drain,
            SymbolDirectory symbols) =>
        {
            if (drain.IsDraining)
            {
                MetricsRegistry.DrainRejections.Add(1,
                    new KeyValuePair<string, object?>("route", "POST /algo"));
                return Results.Json(
                    new { error = "service draining" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(req.Symbol))
                return Results.BadRequest(new { error = "symbol is required" });
            if (!Enum.TryParse<OrderSide>(req.Side, ignoreCase: true, out var side))
                return Results.BadRequest(new { error = $"invalid side '{req.Side}'" });
            if (!Enum.TryParse<AlgoType>(req.Type, ignoreCase: true, out var type))
                return Results.BadRequest(new { error = $"invalid type '{req.Type}'" });
            if (req.TotalQuantity <= 0)
                return Results.BadRequest(new { error = "totalQuantity must be positive" });

            var securityId = req.SecurityId;
            if (securityId == 0 && symbols.TryResolve(req.Symbol, out var resolved))
                securityId = resolved;
            if (securityId == 0)
                return Results.BadRequest(new { error = "securityId is required" });

            // Type-specific parameter validation. v0 keeps the rules
            // explicit + duplicated rather than hiding them behind a
            // visitor — the surface is small and the failure messages
            // matter for trader UI feedback.
            AlgoParameters parameters;
            switch (type)
            {
                case AlgoType.Iceberg:
                    if (req.Iceberg is null)
                        return Results.BadRequest(new { error = "iceberg parameters are required for type=Iceberg" });
                    if (req.Iceberg.DisplayQuantity <= 0)
                        return Results.BadRequest(new { error = "iceberg.displayQuantity must be positive" });
                    if (req.Iceberg.DisplayQuantity > req.TotalQuantity)
                        return Results.BadRequest(new { error = "iceberg.displayQuantity must be <= totalQuantity" });
                    parameters = new IcebergParameters(req.Iceberg.DisplayQuantity, req.Iceberg.LimitPrice);
                    break;

                case AlgoType.Twap:
                    if (req.Twap is null)
                        return Results.BadRequest(new { error = "twap parameters are required for type=Twap" });
                    if (!Enum.TryParse<OrderType>(req.Twap.ChildOrderType, ignoreCase: true, out var childType))
                        return Results.BadRequest(new { error = $"invalid twap.childOrderType '{req.Twap.ChildOrderType}'" });
                    if (childType is not (OrderType.Limit or OrderType.Market))
                        return Results.BadRequest(new { error = "twap.childOrderType must be Limit or Market" });
                    if (req.Twap.EndUtc <= req.Twap.StartUtc)
                        return Results.BadRequest(new { error = "twap.endUtc must be greater than twap.startUtc" });
                    if (req.Twap.SliceCount <= 0)
                        return Results.BadRequest(new { error = "twap.sliceCount must be positive" });
                    // OQ-2: TWAP+Limit MUST carry a child price; otherwise the engine
                    // would have no fallback and the parent could only ever submit
                    // unpriced child orders that contradict the user's chosen type.
                    if (childType == OrderType.Limit && req.Twap.ChildPrice is null)
                        return Results.BadRequest(new { error = "twap.childPrice is required when twap.childOrderType is Limit" });
                    parameters = new TwapParameters(
                        req.Twap.StartUtc, req.Twap.EndUtc, req.Twap.SliceCount,
                        childType, req.Twap.ChildPrice);
                    break;

                default:
                    return Results.BadRequest(new { error = $"unsupported algo type '{type}'" });
            }

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            var algoId = algoIds.Generate(firm);
            var createdAt = DateTimeOffset.UtcNow;
            var algo = new Algo(algoId, owner, firm, req.Symbol, securityId,
                side, type, req.TotalQuantity, parameters, createdAt);

            try
            {
                dispatcher.Dispatch(
                    new AlgoCreatedEvent
                    {
                        AlgoId = algoId,
                        EndClientId = owner.Value,
                        FirmId = firm,
                        Symbol = req.Symbol,
                        SecurityId = securityId,
                        Side = side.ToString(),
                        Type = type.ToString(),
                        TotalQuantity = req.TotalQuantity,
                        CreatedAtUtc = createdAt,
                        IcebergDisplayQuantity = (parameters as IcebergParameters)?.DisplayQuantity,
                        IcebergLimitPrice = (parameters as IcebergParameters)?.LimitPrice,
                        TwapStartUtc = (parameters as TwapParameters)?.StartUtc,
                        TwapEndUtc = (parameters as TwapParameters)?.EndUtc,
                        TwapSliceCount = (parameters as TwapParameters)?.SliceCount,
                        TwapChildOrderType = (parameters as TwapParameters)?.ChildOrderType.ToString(),
                        TwapChildPrice = (parameters as TwapParameters)?.ChildPrice,
                    },
                    () => algos.TryAdd(algo));
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "algo.submit"));
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            sink.PublishAlgoSnapshot(owner, firm, algoId);

            return Results.Accepted($"/algo/{algoId}", new
            {
                AlgoId = algoId.ToString(),
                Status = AlgoStatus.PendingNew.ToString(),
            });
        });

        group.MapDelete("/{algoId}", (
            string algoId,
            HttpContext ctx,
            EndClientRegistry registry,
            AlgoBook algos,
            IAlgoEventSink sink,
            EventDispatcher dispatcher,
            DrainState drain) =>
        {
            if (drain.IsDraining)
            {
                MetricsRegistry.DrainRejections.Add(1,
                    new KeyValuePair<string, object?>("route", "DELETE /algo"));
                return Results.Json(
                    new { error = "service draining" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!ulong.TryParse(algoId, out var id) || id == 0)
                return Results.NotFound();

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            if (!algos.TryGet(firm, id, out var algo) || algo is null || algo.Owner != owner)
                return Results.NotFound();

            // Already-terminal parents can't be cancelled; terminal-on-terminal
            // is a 409 so the caller can distinguish "you're racing the engine"
            // from "the algo doesn't exist". Cancelling-on-Cancelling is OK
            // (idempotent — operator may DELETE multiple times during a slow
            // venue cancel and should always see a 202).
            if (algo.IsTerminal)
                return Results.Conflict(new { error = $"algo {id} is already terminal in {algo.Status}" });

            var actorUserId = ctx.User.FindFirstValue(ClaimTypes.Name)
                ?? ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

            try
            {
                dispatcher.Dispatch(
                    new AlgoCancelRequestedEvent
                    {
                        AlgoId = id,
                        FirmId = firm,
                        ActorUserId = actorUserId,
                    },
                    () => algo.RequestCancel());
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "algo.cancel"));
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            sink.PublishAlgoSnapshot(owner, firm, id);

            return Results.Accepted($"/algo/{id}", new
            {
                AlgoId = id.ToString(),
                Status = AlgoStatus.Cancelling.ToString(),
            });
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

/// <summary>
/// HTTP request body for <c>POST /algo</c>. The discriminated parameter
/// shape mirrors the wire <see cref="AlgoDto"/>: only the block matching
/// <see cref="Type"/> is read; the other is ignored. Per RFC §C2 the
/// public schema deliberately does NOT expose <c>parentAlgoId</c> /
/// <c>algoSliceSeq</c> — algo-on-algo is forbidden in v0 and the engine
/// uses an internal pipeline for child submission, never this endpoint.
/// </summary>
public sealed record CreateAlgoRequest(
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long TotalQuantity,
    CreateAlgoIcebergParams? Iceberg,
    CreateAlgoTwapParams? Twap);

public sealed record CreateAlgoIcebergParams(long DisplayQuantity, decimal? LimitPrice);

public sealed record CreateAlgoTwapParams(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int SliceCount,
    string ChildOrderType,
    decimal? ChildPrice);
