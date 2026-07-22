using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;

using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
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
        var group = app.MapGroup("/api/algo").RequireAuthorization();

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
            IOutboundRecoveryGate recovery,
            SymbolDirectory symbols,
            IAlgoSignalQueue signals,
            ITickSizeProvider tickSizes) =>
        {
            var firm = ResolveFirm(ctx);
            if (!recovery.IsBusinessIngressOpen(firm))
                return RecoveryUnavailable();
            if (drain.IsDraining)
            {
                MetricsRegistry.DrainRejections.Add(1,
                    new KeyValuePair<string, object?>("route", "POST /api/algo"));
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

            // #518. Lot-size gate mirroring the per-order MinLotSizeCheck.
            // The algo parent never hits the risk pipeline, so without this
            // an off-lot total would only surface when the engine submits an
            // off-lot child — which the venue rejects, terminally suspending
            // the parent. Validating the total (and, below, slice-derived
            // quantities) up front keeps every algo child a whole lot.
            // TWAP is excluded here: its §4.8 branch performs the same
            // off-lot-total check but returns the richer rejection body
            // (impliedSliceQuantity / totalQuantity / sliceCount echo).
            long lotSize = 1;
            if (symbols.TryGetSpec(req.Symbol, out var instrumentSpec)
                && instrumentSpec.LotSize is { } configuredLot
                && configuredLot > 1)
            {
                lotSize = configuredLot;
            }
            if (lotSize > 1 && type != AlgoType.Twap && req.TotalQuantity % lotSize != 0)
                return Results.BadRequest(new
                {
                    error = $"totalQuantity {req.TotalQuantity} is not a multiple of lot size {lotSize} for {req.Symbol}",
                    code = "min_lot_size",
                });

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
                    // #518. The display quantity is the child order size, so
                    // it must itself be a whole lot or every iceberg child is
                    // risk-rejected.
                    if (lotSize > 1 && req.Iceberg.DisplayQuantity % lotSize != 0)
                        return Results.BadRequest(new
                        {
                            error = $"iceberg.displayQuantity {req.Iceberg.DisplayQuantity} is not a multiple of lot size {lotSize} for {req.Symbol}",
                            code = "min_lot_size",
                        });
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
                    // RFC §4.8 / #518: reject when the implied per-slice
                    // quantity rounds to zero — or, on a round-lot
                    // instrument, when the total itself is not a whole
                    // number of lots (an off-lot total would otherwise
                    // strand an off-lot remainder on the last slice). Both
                    // are computed in lot units when a lot is configured.
                    // Echo the floor / total / sliceCount in the error body
                    // so the caller can fix either input without guessing.
                    var floorQty = TwapPlan.FloorSliceQty(req.TotalQuantity, req.Twap.SliceCount, lotSize);
                    var offLotTotal = lotSize > 1 && req.TotalQuantity % lotSize != 0;
                    if (floorQty <= 0 || offLotTotal)
                        return Results.BadRequest(new
                        {
                            error = offLotTotal
                                ? $"totalQuantity {req.TotalQuantity} is not a multiple of lot size {lotSize} for {req.Symbol}"
                                : lotSize > 1
                                    ? $"twap.sliceCount produces a per-slice quantity below one lot ({lotSize}) for {req.Symbol}"
                                    : "twap.sliceCount produces a per-slice quantity of zero",
                            impliedSliceQuantity = floorQty,
                            totalQuantity = req.TotalQuantity,
                            sliceCount = req.Twap.SliceCount,
                        });
                    parameters = new TwapParameters(
                        req.Twap.StartUtc, req.Twap.EndUtc, req.Twap.SliceCount,
                        childType, req.Twap.ChildPrice);
                    break;

                case AlgoType.Vwap:
                    // Q3.1 (#281). VWAP mirrors TWAP's surface conventions
                    // — kept on the same POST /api/algo endpoint so the
                    // discriminator-by-Type pattern stays consistent.
                    if (req.Vwap is null)
                        return Results.BadRequest(new { error = "vwap parameters are required for type=Vwap" });
                    if (!Enum.TryParse<OrderType>(req.Vwap.ChildOrderType, ignoreCase: true, out var vwapChildType))
                        return Results.BadRequest(new { error = $"invalid vwap.childOrderType '{req.Vwap.ChildOrderType}'" });
                    if (vwapChildType is not (OrderType.Limit or OrderType.Market))
                        return Results.BadRequest(new { error = "vwap.childOrderType must be Limit or Market" });
                    if (req.Vwap.EndUtc <= req.Vwap.StartUtc)
                        return Results.BadRequest(new { error = "vwap.endUtc must be greater than vwap.startUtc" });
                    if (vwapChildType == OrderType.Limit && req.Vwap.ChildPrice is null)
                        return Results.BadRequest(new { error = "vwap.childPrice is required when vwap.childOrderType is Limit" });
                    var tickSeconds = req.Vwap.TickIntervalSeconds ?? 30d;
                    if (tickSeconds <= 0)
                        return Results.BadRequest(new { error = "vwap.tickIntervalSeconds must be positive" });
                    if (req.Vwap.SliceMaxPct is { } smp && (smp <= 0 || smp > 1))
                        return Results.BadRequest(new { error = "vwap.sliceMaxPct must be in (0, 1]" });
                    if (req.Vwap.ParticipationCap is { } pc && (pc <= 0 || pc > 1))
                        return Results.BadRequest(new { error = "vwap.participationCap must be in (0, 1]" });
                    parameters = new VwapParameters(
                        req.Vwap.StartUtc,
                        req.Vwap.EndUtc,
                        vwapChildType,
                        req.Vwap.ChildPrice,
                        TimeSpan.FromSeconds(tickSeconds),
                        req.Vwap.SliceMaxPct,
                        req.Vwap.PriceLimit,
                        req.Vwap.ParticipationCap);
                    break;

                case AlgoType.Pov:
                    // Q3.2 (#282). POV mirrors VWAP's surface conventions
                    // — same POST /api/algo endpoint, discriminator-by-Type
                    // pattern.
                    if (req.Pov is null)
                        return Results.BadRequest(new { error = "pov parameters are required for type=Pov" });
                    if (!Enum.TryParse<OrderType>(req.Pov.ChildOrderType, ignoreCase: true, out var povChildType))
                        return Results.BadRequest(new { error = $"invalid pov.childOrderType '{req.Pov.ChildOrderType}'" });
                    if (povChildType is not (OrderType.Limit or OrderType.Market))
                        return Results.BadRequest(new { error = "pov.childOrderType must be Limit or Market" });
                    if (req.Pov.EndUtc <= req.Pov.StartUtc)
                        return Results.BadRequest(new { error = "pov.endUtc must be greater than pov.startUtc" });
                    if (povChildType == OrderType.Limit && req.Pov.ChildPrice is null)
                        return Results.BadRequest(new { error = "pov.childPrice is required when pov.childOrderType is Limit" });
                    if (req.Pov.ParticipationRate <= 0m || req.Pov.ParticipationRate > 1m)
                        return Results.BadRequest(new { error = "pov.participationRate must be in (0, 1]" });
                    var povTickSeconds = req.Pov.TickIntervalSeconds ?? 5d;
                    if (povTickSeconds <= 0)
                        return Results.BadRequest(new { error = "pov.tickIntervalSeconds must be positive" });
                    var povMinSlice = req.Pov.MinSliceQty ?? 1L;
                    if (povMinSlice < 1)
                        return Results.BadRequest(new { error = "pov.minSliceQty must be >= 1" });
                    parameters = new PovParameters(
                        req.Pov.StartUtc,
                        req.Pov.EndUtc,
                        povChildType,
                        req.Pov.ChildPrice,
                        req.Pov.ParticipationRate,
                        TimeSpan.FromSeconds(povTickSeconds),
                        req.Pov.PriceLimit,
                        povMinSlice);
                    break;

                case AlgoType.Pegged:
                    // Q3.3 (#283). Pegged shares the POST /api/algo surface
                    // with the other algos via the type discriminator.
                    // #454 Fase 1: tick now resolves through
                    // ITickSizeProvider (config-backed SymbolDirectory
                    // today; SDK-backed in Fase 2 once upstream
                    // pedrosakuma/B3MarketDataPlatform#55 ships
                    // SecurityDefinitionEvent). The request body keeps
                    // its explicit override (operator wins), but the
                    // legacy 0.01m BRL-equity floor is gone — a missing
                    // tick now surfaces as an explicit reject instead
                    // of a silent fallback that could mismatch the venue.
                    if (req.Pegged is null)
                        return Results.BadRequest(new { error = "pegged parameters are required for type=Pegged" });
                    if (!Enum.TryParse<PegRef>(req.Pegged.Ref, ignoreCase: true, out var peggedRef))
                        return Results.BadRequest(new { error = $"invalid pegged.ref '{req.Pegged.Ref}'; expected Mid|Best|Last" });
                    var peggedRepegMs = req.Pegged.RepegIntervalMs ?? 500;
                    if (peggedRepegMs <= 0)
                        return Results.BadRequest(new { error = "pegged.repegIntervalMs must be positive" });
                    decimal peggedTickSize;
                    if (req.Pegged.TickSize is { } overrideTick)
                    {
                        if (overrideTick <= 0m)
                            return Results.BadRequest(new { error = "pegged.tickSize must be positive" });
                        peggedTickSize = overrideTick;
                    }
                    else if (tickSizes.TryGetTickSize(req.Symbol, req.Pegged.PriceLimit, out var resolvedTick))
                    {
                        peggedTickSize = resolvedTick;
                    }
                    else
                    {
                        return Results.BadRequest(new { error = $"pegged.tickSize is required for symbol '{req.Symbol}' (no per-symbol tick configured)" });
                    }
                    var peggedChildType = OrderType.Limit;
                    if (!string.IsNullOrWhiteSpace(req.Pegged.ChildOrderType))
                    {
                        if (!Enum.TryParse<OrderType>(req.Pegged.ChildOrderType, ignoreCase: true, out peggedChildType))
                            return Results.BadRequest(new { error = $"invalid pegged.childOrderType '{req.Pegged.ChildOrderType}'" });
                        // Market orders defeat the whole point of pegging
                        // (the venue picks the price). Reject explicitly
                        // rather than letting it silently slip through.
                        if (peggedChildType != OrderType.Limit)
                            return Results.BadRequest(new { error = "pegged.childOrderType must be Limit" });
                    }
                    parameters = new PeggedParameters(
                        peggedRef,
                        req.Pegged.OffsetTicks,
                        TimeSpan.FromMilliseconds(peggedRepegMs),
                        peggedTickSize,
                        peggedChildType,
                        req.Pegged.PriceLimit);
                    break;

                default:
                    return Results.BadRequest(new { error = $"unsupported algo type '{type}'" });
            }
            var owner = ResolveOwner(ctx, registry);
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
                        VwapStartUtc = (parameters as VwapParameters)?.StartUtc,
                        VwapEndUtc = (parameters as VwapParameters)?.EndUtc,
                        VwapChildOrderType = (parameters as VwapParameters)?.ChildOrderType.ToString(),
                        VwapChildPrice = (parameters as VwapParameters)?.ChildPrice,
                        VwapTickIntervalTicks = (parameters as VwapParameters)?.TickInterval.Ticks,
                        VwapSliceMaxPct = (parameters as VwapParameters)?.SliceMaxPct,
                        VwapPriceLimit = (parameters as VwapParameters)?.PriceLimit,
                        VwapParticipationCap = (parameters as VwapParameters)?.ParticipationCap,
                        PovStartUtc = (parameters as PovParameters)?.StartUtc,
                        PovEndUtc = (parameters as PovParameters)?.EndUtc,
                        PovChildOrderType = (parameters as PovParameters)?.ChildOrderType.ToString(),
                        PovChildPrice = (parameters as PovParameters)?.ChildPrice,
                        PovParticipationRate = (parameters as PovParameters)?.ParticipationRate,
                        PovTickIntervalTicks = (parameters as PovParameters)?.TickInterval.Ticks,
                        PovPriceLimit = (parameters as PovParameters)?.PriceLimit,
                        PovMinSliceQty = (parameters as PovParameters)?.MinSliceQty,
                        PeggedRef = (parameters as PeggedParameters)?.Ref.ToString(),
                        PeggedOffsetTicks = (parameters as PeggedParameters)?.OffsetTicks,
                        PeggedRepegIntervalTicks = (parameters as PeggedParameters)?.RepegInterval.Ticks,
                        PeggedTickSize = (parameters as PeggedParameters)?.TickSize,
                        PeggedChildOrderType = (parameters as PeggedParameters)?.ChildOrderType.ToString(),
                        PeggedPriceLimit = (parameters as PeggedParameters)?.PriceLimit,
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

            if (!signals.TryEnqueue(new AlgoCreatedSignal { FirmId = firm, AlgoId = algoId }))
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", "created"));
            }

            return Results.Accepted($"/api/algo/{algoId}", new
            {
                AlgoId = algoId.ToString(),
                Status = AlgoStatus.PendingNew.ToString(),
            });
        });

        // Q3.5 (#285). Operator-driven cancel-replace (modify) of a
        // live algo child. Mirrors the cancel endpoint's auth /
        // ownership / drain semantics; the modify intent is enqueued
        // on the engine signal queue and applied on the consumer
        // thread so per-parent serialisation is preserved (the same
        // reactor that submits new slices also dispatches modifies,
        // so an in-flight repeg and an operator modify can't race
        // each other into a double cancel-replace).
        group.MapPost("/{algoId}/modify", (
            string algoId,
            ModifyAlgoRequest? req,
            HttpContext ctx,
            EndClientRegistry registry,
            AlgoBook algos,
            DrainState drain,
            IOutboundRecoveryGate recovery,
            IAlgoSignalQueue signals) =>
        {
            var firm = ResolveFirm(ctx);
            if (!recovery.IsBusinessIngressOpen(firm))
                return RecoveryUnavailable();
            if (drain.IsDraining)
            {
                MetricsRegistry.DrainRejections.Add(1,
                    new KeyValuePair<string, object?>("route", "POST /api/algo/modify"));
                return Results.Json(
                    new { error = "service draining" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!ulong.TryParse(algoId, out var id) || id == 0)
                return Results.NotFound();
            if (req is null)
                return Results.BadRequest(new { error = "request body required" });
            if (req.NewQuantity is null && req.NewPrice is null)
                return Results.BadRequest(new { error = "at least one of newQuantity or newPrice must be set" });
            if (req.NewQuantity is { } q && q <= 0)
                return Results.BadRequest(new { error = "newQuantity must be positive" });
            var owner = ResolveOwner(ctx, registry);
            if (!algos.TryGet(firm, id, out var algo) || algo is null || algo.Owner != owner)
                return Results.NotFound();
            if (algo.IsTerminal)
            {
                MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                    new KeyValuePair<string, object?>("algoType", algo.Type.ToString().ToLowerInvariant()),
                    new KeyValuePair<string, object?>("reason", "algo_terminal"));
                return Results.Conflict(new { error = $"algo {id} is already terminal in {algo.Status}" });
            }
            if (algo.Status == AlgoStatus.Cancelling)
            {
                MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                    new KeyValuePair<string, object?>("algoType", algo.Type.ToString().ToLowerInvariant()),
                    new KeyValuePair<string, object?>("reason", "algo_cancelling"));
                return Results.Conflict(new { error = $"algo {id} is cancelling" });
            }

            var reason = ModifyAlgoRequest.NormalizeReason(req.Reason);
            if (reason is null)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_reason",
                    detail = $"reason must be one of: {string.Join(", ", ModifyAlgoRequest.AllowedReasons)}",
                });
            }
            if (!signals.TryEnqueue(new AlgoModifyRequestedSignal
            {
                FirmId = firm,
                AlgoId = id,
                TargetChildClOrdId = req.ChildClOrdId,
                NewQuantity = req.NewQuantity,
                NewPrice = req.NewPrice,
                Reason = reason,
            }))
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", "modify_requested"));
                return Results.Json(
                    new { error = "engine busy (signal queue full)" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Accepted($"/api/algo/{id}", new
            {
                AlgoId = id.ToString(),
                Status = algo.Status.ToString(),
                Reason = reason,
            });
        });

        group.MapDelete("/{algoId}", (
            string algoId,
            HttpContext ctx,
            EndClientRegistry registry,
            AlgoBook algos,
            IAlgoEventSink sink,
            EventDispatcher dispatcher,
            DrainState drain,
            IOutboundRecoveryGate recovery,
            IAlgoSignalQueue signals) =>
        {
            var firm = ResolveFirm(ctx);
            if (!recovery.IsBusinessIngressOpen(firm))
                return RecoveryUnavailable();
            if (drain.IsDraining)
            {
                MetricsRegistry.DrainRejections.Add(1,
                    new KeyValuePair<string, object?>("route", "DELETE /api/algo"));
                return Results.Json(
                    new { error = "service draining" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!ulong.TryParse(algoId, out var id) || id == 0)
                return Results.NotFound();
            var owner = ResolveOwner(ctx, registry);
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

            if (!signals.TryEnqueue(new AlgoCancelRequestedSignal
            {
                FirmId = firm,
                AlgoId = id,
                ExplicitRetry = true,
            }))
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", "cancel_requested"));
            }

            return Results.Accepted($"/api/algo/{id}", new
            {
                AlgoId = id.ToString(),
                Status = AlgoStatus.Cancelling.ToString(),
            });
        });

        return app;
    }

    private static IResult RecoveryUnavailable() =>
        Results.Json(
            new
            {
                error = "outbound cold-start recovery is incomplete",
                code = "outbound_recovery_incomplete",
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);

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
/// HTTP request body for <c>POST /api/algo</c>. The discriminated parameter
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
    CreateAlgoTwapParams? Twap,
    CreateAlgoVwapParams? Vwap = null,
    CreateAlgoPovParams? Pov = null,
    CreateAlgoPeggedParams? Pegged = null);

public sealed record CreateAlgoIcebergParams(long DisplayQuantity, decimal? LimitPrice);

public sealed record CreateAlgoTwapParams(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int SliceCount,
    string ChildOrderType,
    decimal? ChildPrice);

/// <summary>
/// HTTP request shape for the VWAP parameter block (Q3.1 / #281).
/// <see cref="TickIntervalSeconds"/> defaults to 30s when null — matches
/// the issue spec default.
/// </summary>
public sealed record CreateAlgoVwapParams(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string ChildOrderType,
    decimal? ChildPrice,
    double? TickIntervalSeconds = null,
    decimal? SliceMaxPct = null,
    decimal? PriceLimit = null,
    decimal? ParticipationCap = null);

/// <summary>
/// HTTP request shape for the POV parameter block (Q3.2 / #282).
/// <see cref="TickIntervalSeconds"/> defaults to 5s when null (issue
/// spec: short bucket for reactivity). <see cref="MinSliceQty"/>
/// defaults to 1.
/// </summary>
public sealed record CreateAlgoPovParams(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string ChildOrderType,
    decimal? ChildPrice,
    decimal ParticipationRate,
    double? TickIntervalSeconds = null,
    decimal? PriceLimit = null,
    long? MinSliceQty = null);

/// <summary>
/// HTTP request shape for the Pegged parameter block (Q3.3 / #283).
/// <para>
/// <b>Defaults.</b> <see cref="RepegIntervalMs"/> defaults to 500ms,
/// <see cref="TickSize"/> is now resolved from
/// <c>ITickSizeProvider</c> when the request omits it (#454 Fase 1 —
/// the legacy 0.01 BRL-equity fallback was removed; explicit override
/// still wins). <see cref="ChildOrderType"/> defaults to <c>Limit</c>
/// (the only legal value — Market would defeat the peg).
/// <see cref="OffsetTicks"/> is required and may be negative for
/// passive pegs (Buy below the bid, Sell above the ask).
/// </para>
/// </summary>
public sealed record CreateAlgoPeggedParams(
    string Ref,
    int OffsetTicks,
    int? RepegIntervalMs = null,
    decimal? TickSize = null,
    string? ChildOrderType = null,
    decimal? PriceLimit = null);

/// <summary>
/// Q3.5 (#285). Body for <c>POST /api/algo/{id}/modify</c>. At least one
/// of <see cref="NewQuantity"/> / <see cref="NewPrice"/> must be set;
/// the engine inherits the omitted side from the live child.
/// <see cref="ChildClOrdId"/> is optional — when null the engine
/// targets the parent's current live child (the common case).
/// <see cref="Reason"/> is folded into metrics + the
/// <c>AlgoChildModifiedEvent</c> audit envelope; defaults to
/// <c>operator</c> when omitted.
/// </summary>
public sealed record ModifyAlgoRequest(
    long? NewQuantity = null,
    decimal? NewPrice = null,
    ulong? ChildClOrdId = null,
    string? Reason = null)
{
    // Pass-1 review (#299) P2-A. Reason flows into a metric tag, so it
    // MUST be a closed enum to avoid unbounded cardinality from
    // arbitrary operator strings. Audit (AlgoChildModifiedEvent) still
    // records the normalised value verbatim — same identifier, just
    // constrained. Defaults to OperatorModify when omitted; anything
    // outside this allowlist is rejected at the endpoint with 400.
    public static readonly IReadOnlyList<string> AllowedReasons = new[]
    {
        "OperatorModify",
        "AlgoInternal",
        "Reconciliation",
    };

    /// <summary>
    /// Trims and case-insensitively matches <paramref name="raw"/> against
    /// <see cref="AllowedReasons"/>. Returns the canonical-cased value on
    /// match, the default (<c>OperatorModify</c>) on null/whitespace, or
    /// <c>null</c> when the input does not match any allowed reason.
    /// </summary>
    public static string? NormalizeReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AllowedReasons[0];
        var trimmed = raw.Trim();
        foreach (var allowed in AllowedReasons)
        {
            if (string.Equals(trimmed, allowed, StringComparison.OrdinalIgnoreCase))
                return allowed;
        }
        return null;
    }
}
