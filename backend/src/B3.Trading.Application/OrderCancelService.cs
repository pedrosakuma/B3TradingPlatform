using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application;

/// <summary>
/// Sub-issue #171 (E). Centralised cancel-order pipeline shared by both
/// <c>DELETE /orders/{clOrdId}</c> (REST) and the FIXP listener's
/// <c>OrderCancelRequest</c> handler. Replaces the previous in-memory-only
/// path (<c>OwnershipMap.RegisterCancelLink</c>) with a WAL-durable
/// <see cref="OrderCancelRequestedEvent"/> append, so cancel-side state
/// (cancel-link, ClOrdID watermark, optional bot-cancel-mapping) survives
/// restart per RFC user-bot-fixp-listener-v0 §4.6 and §4.8.
///
/// <para>The dispatcher <c>apply</c> callback runs synchronous in-memory
/// mutations only; the async <c>gateway.CancelAsync</c> I/O is invoked
/// AFTER <see cref="EventDispatcher.Dispatch"/> returns, OUTSIDE the
/// dispatcher lock — the dispatcher protects synchronous in-memory work
/// only. On replay the same callback runs and rebuilds in-memory state
/// without re-issuing the gateway call.</para>
/// </summary>
public sealed class OrderCancelService
{
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly OrderOwnershipMap _ownership;
    private readonly WorkingOrderBook _book;
    private readonly IExchangeGateway _gateway;
    private readonly EventDispatcher _dispatcher;
    private readonly IUserBotOrderMappingRegistry? _botMappings;
    private readonly ILogger<OrderCancelService> _logger;

    public OrderCancelService(
        ClOrdIdPrefixRegistry clOrdIds,
        OrderOwnershipMap ownership,
        WorkingOrderBook book,
        IExchangeGateway gateway,
        EventDispatcher dispatcher,
        ILogger<OrderCancelService> logger,
        IUserBotOrderMappingRegistry? botMappings = null)
    {
        _clOrdIds = clOrdIds;
        _ownership = ownership;
        _book = book;
        _gateway = gateway;
        _dispatcher = dispatcher;
        _botMappings = botMappings;
        _logger = logger;
    }

    /// <summary>
    /// Cancels the order owned by <paramref name="owner"/> with id
    /// <paramref name="originalClOrdId"/>. Returns the platform-allocated
    /// cancel-side internal ClOrdID on accept, or a discriminated failure
    /// otherwise. The caller (REST endpoint or FIXP listener) translates
    /// the result into its own transport response.
    /// </summary>
    public async Task<OrderCancelResult> CancelAsync(
        EndClientId owner,
        ulong originalClOrdId,
        CancellationToken ct,
        BotOrigin? botOrigin = null,
        string? firmId = null)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!_book.TryGet(originalClOrdId, out var order) || order is null)
            return OrderCancelResult.NotFound;
        if (order.Owner != owner)
            return OrderCancelResult.NotFound;
        // PR #316 P1. Reject cross-firm cancels (same posture as
        // modify and read paths) — return NotFound so existence is
        // not leaked across the firm boundary.
        if (firmId is not null && !string.Equals(order.FirmId, firmId, StringComparison.Ordinal))
            return OrderCancelResult.NotFound;
        if (order.IsStale)
            return OrderCancelResult.Stale(order.StaleReason ?? "stale");

        var cancelClOrdId = _clOrdIds.Generate(owner);
        BotOrderMapping? botMapping = botOrigin is { } o
            ? new BotOrderMapping(o.CredentialId, o.ExternalClOrdId)
            : null;

        try
        {
            _dispatcher.Dispatch(
                new OrderCancelRequestedEvent
                {
                    CancelClOrdId = cancelClOrdId,
                    OriginalClOrdId = originalClOrdId,
                    OwnerEndClientId = owner.Value,
                    BotMapping = botMapping,
                },
                () =>
                {
                    // RFC §4.6 step 3 — synchronous in-memory mutations
                    // only. Three things in lockstep with the WAL append:
                    _ownership.RegisterCancelLink(cancelClOrdId, originalClOrdId);
                    // Watermark advance so a post-restart Generate cannot
                    // re-allocate this cancel-side id (#157 invariant for
                    // cancels — mirrors OrderReplaceRequestedEvent path).
                    _clOrdIds.AdvanceCounterTo(owner, cancelClOrdId);
                    if (botMapping is not null && _botMappings is not null)
                    {
                        _botMappings.RegisterCancelInternal(
                            cancelInternalClOrdId: cancelClOrdId,
                            originalInternalClOrdId: originalClOrdId,
                            credentialId: botMapping.CredentialId,
                            externalCancelClOrdId: botMapping.ExternalClOrdId);
                    }
                });
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "orders.cancel"));
            return OrderCancelResult.WalBackpressure(ex.Message);
        }

        try
        {
            // RFC §4.6 step 4: gateway call OUTSIDE the dispatcher lock
            // — Dispatch has already returned and the WAL record is
            // enqueued + the in-memory state is consistent.
            await _gateway.CancelAsync(order, cancelClOrdId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The cancel WAL record is already durable; leaving it as
            // "in-flight" is the same posture the legacy in-memory path
            // had on a gateway hiccup — eventual ER reconciliation or
            // operator intervention resolves it.
            _logger.LogError(ex,
                "Gateway cancel failed for {ClOrdId} (orig {Orig}); WAL already durable.",
                cancelClOrdId, originalClOrdId);
            return OrderCancelResult.GatewayFailed(cancelClOrdId, ex);
        }

        MetricsRegistry.OrdersCancelRequested.Add(1,
            new KeyValuePair<string, object?>("firmId", order.FirmId));
        return OrderCancelResult.Accepted(cancelClOrdId);
    }
}

/// <summary>
/// Outcome of <see cref="OrderCancelService.CancelAsync"/>.
/// Discriminated on <see cref="Kind"/>; callers (REST endpoint, FIXP
/// listener) map each case to their own transport.
/// </summary>
public sealed class OrderCancelResult
{
    public OrderCancelResultKind Kind { get; }
    public ulong CancelClOrdId { get; }
    public string? Reason { get; }
    public Exception? GatewayException { get; }

    private OrderCancelResult(OrderCancelResultKind kind, ulong cancelClOrdId, string? reason, Exception? ex)
    {
        Kind = kind;
        CancelClOrdId = cancelClOrdId;
        Reason = reason;
        GatewayException = ex;
    }

    public static OrderCancelResult Accepted(ulong cancelClOrdId) =>
        new(OrderCancelResultKind.Accepted, cancelClOrdId, null, null);
    public static OrderCancelResult NotFound { get; } =
        new(OrderCancelResultKind.NotFound, 0, null, null);
    public static OrderCancelResult Stale(string reason) =>
        new(OrderCancelResultKind.Stale, 0, reason, null);
    public static OrderCancelResult WalBackpressure(string detail) =>
        new(OrderCancelResultKind.WalBackpressure, 0, detail, null);
    public static OrderCancelResult GatewayFailed(ulong cancelClOrdId, Exception ex) =>
        new(OrderCancelResultKind.GatewayFailed, cancelClOrdId, "gateway_unavailable", ex);
}

public enum OrderCancelResultKind
{
    Accepted,
    NotFound,
    Stale,
    WalBackpressure,
    GatewayFailed,
}
