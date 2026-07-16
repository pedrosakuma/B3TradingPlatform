using System.Buffers;

using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Framing;
using Microsoft.Extensions.Logging;

// Q1.1 (#253). Both SBE V6 and Domain expose a `TimeInForce` type;
// alias them to disambiguate references inside this file.
using DomainTif = B3.Trading.Domain.TimeInForce;
using SbeTif = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #171 (E). Adapter that translates inbound SBE
/// <c>NewOrderSingle</c> / <c>OrderCancelRequest</c> messages into the
/// platform's <see cref="OrderSubmissionService"/> /
/// <see cref="OrderCancelService"/> calls, and writes back synthetic
/// <c>BusinessMessageReject</c> / <c>ExecutionReport_Reject</c> frames
/// for the failure paths described in RFC user-bot-fixp-listener-v0
/// §4.6 / §4.7.
///
/// <para>Stateless: all per-connection state lives in the
/// <see cref="FixpConnectionScope"/> (principal) and the platform-side
/// registries. The adapter holds only the cross-connection singletons
/// it dispatches into. <c>Handle...Async</c> takes the
/// <see cref="NetworkStream"/> from <see cref="FixpSessionConnection"/>
/// to write synchronous reject paths and returns a disposition when the
/// owning session must close for reconciliation.</para>
/// </summary>
internal sealed class FixpOrderAdapter
{
    private const ushort SchemaIdV6 = 1;
    private const ushort VersionApp = 6;

    /// <summary>
    /// RFC §4.6 / §4.7. Synthetic rejection reason codes the listener
    /// emits in <c>BusinessMessageReject.businessRejectReason</c> and
    /// <c>ExecutionReport_Reject.ordRejReason</c>. Values are local to
    /// the platform — the FIXP spec leaves the field as a free-form
    /// uint32 reason code and bots only need them to be stable for
    /// log-correlation.
    /// </summary>
    private static class RejectReason
    {
        public const uint UnknownSecurity = 1001;
        public const uint InvalidShape = 1002;
        public const uint DuplicateClOrdId = 1003;
        public const uint UnknownOrder = 1004;
        public const uint Drained = 1005;
        public const uint WalBackpressure = 1006;
        public const uint RiskRejected = 1007;
        public const uint GatewayFailed = 1008;
        public const uint BadRequest = 1009;
        public const uint StaleOrder = 1010;
        public const uint ReconciliationRequired = 1011;
    }

    private readonly SymbolDirectory _symbols;
    private readonly OrderSubmissionService _submit;
    private readonly OrderCancelService _cancel;
    private readonly IUserBotOrderMappingRegistry _botMappings;
    private readonly ILogger _logger;

    public FixpOrderAdapter(
        SymbolDirectory symbols,
        OrderSubmissionService submit,
        OrderCancelService cancel,
        IUserBotOrderMappingRegistry botMappings,
        ILogger logger)
    {
        _symbols = symbols;
        _submit = submit;
        _cancel = cancel;
        _botMappings = botMappings;
        _logger = logger;
    }

    /// <summary>
    /// Decode an inbound <c>NewOrderSingle</c> (id=102) and dispatch it
    /// through <see cref="OrderSubmissionService.SubmitAsync"/>. Returns
    /// a disposition telling the owning connection whether it may continue.
    /// An unreconciled durable terminal failure emits a non-terminal
    /// <c>BusinessMessageReject</c> and requests session close.
    /// </summary>
    public async Task<FixpOrderHandlingResult> HandleNewOrderSingleAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        FixpConnectionScope scope,
        CancellationToken ct)
    {
        // Legacy entry point retained for the malformed-length fall-through
        // and any caller that has not yet adopted the zero-copy decode.
        // Hot in-Established traffic now flows through the
        // `in DecodedNewOrderSingle` overload below (RFC §5.6 / P10).
        if (!InboundDecoders.TryDecodeNewOrderSingle(payload.Span, out var decoded))
        {
            await WriteBusinessMessageRejectAsync(stream,
                refMsgType: MessageType.NewOrderSingle,
                refSeqNum: 0, businessRejectRefID: 0,
                reason: RejectReason.InvalidShape, ct).ConfigureAwait(false);
            return FixpOrderHandlingResult.Keep;
        }
        return await HandleNewOrderSingleAsync(stream, decoded, scope, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RFC §5.6 (P10/F6) zero-copy entry point. The dispatcher has
    /// already decoded the SBE block into <paramref name="decoded"/>;
    /// no <c>byte[]</c> survives across the awaits below.
    /// </summary>
    public async Task<FixpOrderHandlingResult> HandleNewOrderSingleAsync(
        Stream stream,
        DecodedNewOrderSingle decoded,
        FixpConnectionScope scope,
        CancellationToken ct)
    {
        var externalClOrdId = decoded.ClOrdId;
        var securityId = decoded.SecurityId;
        var refSeqNum = decoded.MsgSeqNum;

        // 1. Resolve SecurityId → Symbol. Without a directory entry the
        //    submit pipeline would reject anyway with "symbol is required";
        //    we short-circuit with the more precise UnknownSecurity reason.
        if (!_symbols.TryGetSymbolBySecurityId(securityId, out var symbol) || symbol is null)
        {
            _logger.LogInformation(
                "fixp.order.reject reason=unknown_security cred={Cred} clOrdId={ClOrdId} securityId={SecId}",
                scope.Principal.CredShortId, externalClOrdId, securityId);
            await WriteBusinessMessageRejectAsync(stream,
                MessageType.NewOrderSingle, refSeqNum, externalClOrdId,
                RejectReason.UnknownSecurity, ct).ConfigureAwait(false);
            return FixpOrderHandlingResult.Keep;
        }

        // 2. Validate side/ordType + TIF up-front so a malformed wire
        //    byte is a clean BMR rather than a generic BadRequest from
        //    the pipeline. Stop variants and the full TIF set are
        //    accepted at the wire (Q1.1 / #253) — domain-side cross-
        //    field invariants (StopPrice required for Stop*; ExpireDate
        //    required for GTD) are checked inside the Order ctor and
        //    bubble back up as BadRequest below.
        if (!TryMapSide(decoded.Side, out var side)
            || !TryMapOrdType(decoded.OrdType, out var type)
            || !TryMapTimeInForce(decoded.TimeInForce, out var tif))
        {
            await WriteBusinessMessageRejectAsync(stream,
                MessageType.NewOrderSingle, refSeqNum, externalClOrdId,
                RejectReason.InvalidShape, ct).ConfigureAwait(false);
            return FixpOrderHandlingResult.Keep;
        }

        // 3. Pre-emptive duplicate check — a bot retrying the same
        //    ExternalClOrdId is the most likely cause of #108 firing
        //    deep inside Submit. Catching it here lets us send back a
        //    BusinessMessageReject(DuplicateClOrdId) keyed off the
        //    external id without consuming a platform internal id.
        if (_botMappings.TryGetByExternal(scope.Principal.CredentialId, externalClOrdId, out _))
        {
            await WriteBusinessMessageRejectAsync(stream,
                MessageType.NewOrderSingle, refSeqNum, externalClOrdId,
                RejectReason.DuplicateClOrdId, ct).ConfigureAwait(false);
            return FixpOrderHandlingResult.Keep;
        }

        var owner = OwnerFor(scope);
        var qty = (long)decoded.OrderQty;
        decimal? price = decoded.PriceMantissa is { } m
            ? m / (decimal)PriceOptional.Multiplier
            : null;
        // Q1.1 (#253) — StopPx and ExpireDate are optional on the wire.
        // SBE encodes ExpireDate as ushort days since 1970-01-01 with 0
        // as the null sentinel.
        decimal? stopPrice = decoded.StopPxMantissa is { } sm
            ? sm / (decimal)PriceOptional.Multiplier
            : null;
        DateTimeOffset? goodTillDate = decoded.ExpireDateRaw == 0
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(decoded.ExpireDateRaw * 86400L);

        var req = new OrderSubmissionRequest(
            Owner: owner,
            // #431 — firm scope flows from the authenticated FIXP credential
            // (set at credential creation time from the JWT firm claim);
            // legacy credentials hydrate as "default" via the registry.
            FirmId: scope.Principal.FirmId,
            Symbol: symbol,
            SecurityId: securityId,
            Side: side,
            Type: type,
            Quantity: qty,
            Price: price,
            TimeInForce: tif,
            StopPrice: stopPrice,
            GoodTillDate: goodTillDate)
        {
            BotOrigin = new BotOrigin(scope.Principal.CredentialId, externalClOrdId),
        };

        var result = await _submit.SubmitAsync(req, ct).ConfigureAwait(false);
        if (result.Kind == OrderSubmissionResultKind.Accepted)
            return FixpOrderHandlingResult.Keep;

        if (result.Kind == OrderSubmissionResultKind.ReconciliationRequired)
        {
            _logger.LogCritical(
                "fixp.order.reconciliation-required cred={Cred} externalClOrdId={ExternalClOrdId} internalClOrdId={InternalClOrdId}; sending non-terminal BMR and closing session",
                scope.Principal.CredShortId, externalClOrdId, result.ClOrdId);
            await WriteBusinessMessageRejectAsync(
                stream,
                MessageType.NewOrderSingle,
                refSeqNum,
                externalClOrdId,
                RejectReason.ReconciliationRequired,
                ct).ConfigureAwait(false);
            return FixpOrderHandlingResult.CloseForReconciliation(result.ClOrdId);
        }

        // RFC §4.7 — ordinary submit-time rejections produce a synthetic
        // ExecutionReport_Reject so the bot sees the same shape it would
        // have seen for a venue-side reject. ReconciliationRequired was
        // handled above because its order is deliberately non-terminal.
        var reason = result.Kind switch
        {
            OrderSubmissionResultKind.Drained => RejectReason.Drained,
            OrderSubmissionResultKind.BadRequest => RejectReason.BadRequest,
            OrderSubmissionResultKind.WalBackpressure => RejectReason.WalBackpressure,
            OrderSubmissionResultKind.DuplicateClOrdId => RejectReason.DuplicateClOrdId,
            OrderSubmissionResultKind.Rejected => RejectReason.RiskRejected,
            OrderSubmissionResultKind.GatewayFailed => RejectReason.GatewayFailed,
            _ => RejectReason.BadRequest,
        };

        _logger.LogInformation(
            "fixp.order.reject reason={Reason} kind={Kind} cred={Cred} clOrdId={ClOrdId}",
            reason, result.Kind, scope.Principal.CredShortId, externalClOrdId);

        await WriteExecutionReportRejectAsync(stream,
            externalClOrdId: externalClOrdId,
            origExternalClOrdId: 0,
            securityId: securityId,
            side: decoded.Side,
            ordType: decoded.OrdType,
            qty: decoded.OrderQty,
            priceMantissa: decoded.PriceMantissa ?? PriceOptional.MantissaNullValue,
            ordRejReason: reason,
            cxlRejResponseTo: CxlRejResponseTo.NEW,
            ct).ConfigureAwait(false);
        return FixpOrderHandlingResult.Keep;
    }

    /// <summary>
    /// Decode an inbound <c>OrderCancelRequest</c> (id=105) and dispatch
    /// it through <see cref="OrderCancelService.CancelAsync"/> after
    /// resolving the bot's external <c>OrigClOrdID</c> back to the
    /// platform's internal id via the side-mapping registry.
    /// </summary>
    public Task HandleOrderCancelRequestAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        FixpConnectionScope scope,
        CancellationToken ct)
    {
        // Legacy entry point retained for the malformed-length fall-through.
        if (!InboundDecoders.TryDecodeOrderCancelRequest(payload.Span, out var decoded))
        {
            return WriteBusinessMessageRejectAsync(stream,
                MessageType.OrderCancelRequest, 0, 0,
                RejectReason.InvalidShape, ct);
        }
        return HandleOrderCancelRequestAsync(stream, decoded, scope, ct);
    }

    /// <summary>
    /// RFC §5.6 (P10/F6) zero-copy entry point. The dispatcher has
    /// already decoded the SBE block into <paramref name="decoded"/>;
    /// no <c>byte[]</c> survives across the awaits below.
    /// </summary>
    public async Task HandleOrderCancelRequestAsync(
        Stream stream,
        DecodedOrderCancelRequest decoded,
        FixpConnectionScope scope,
        CancellationToken ct)
    {
        var externalCancelClOrdId = decoded.ClOrdId;
        var externalOrigClOrdId = decoded.OrigClOrdId;
        var securityId = decoded.SecurityId;
        var refSeqNum = decoded.MsgSeqNum;

        // Resolve original order via the bot mapping side-registry.
        // The registry's TryGetByExternal already enforces the
        // (credentialId, externalOrigClOrdId) → internal lookup, which
        // doubles as the cross-credential isolation guard (RFC §4.6:
        // a bot can only cancel orders it submitted).
        if (!_botMappings.TryGetByExternal(
                scope.Principal.CredentialId, externalOrigClOrdId, out var internalOrigClOrdId))
        {
            _logger.LogInformation(
                "fixp.cancel.reject reason=unknown_order cred={Cred} clOrdId={ClOrdId} origClOrdId={Orig}",
                scope.Principal.CredShortId, externalCancelClOrdId, externalOrigClOrdId);
            await WriteBusinessMessageRejectAsync(stream,
                MessageType.OrderCancelRequest, refSeqNum, externalCancelClOrdId,
                RejectReason.UnknownOrder, ct).ConfigureAwait(false);
            return;
        }

        var owner = OwnerFor(scope);
        var botOrigin = new BotOrigin(scope.Principal.CredentialId, externalCancelClOrdId);

        var result = await _cancel.CancelAsync(owner, internalOrigClOrdId, ct, botOrigin)
            .ConfigureAwait(false);
        if (result.Kind == OrderCancelResultKind.Accepted) return;

        var reason = result.Kind switch
        {
            OrderCancelResultKind.NotFound => RejectReason.UnknownOrder,
            OrderCancelResultKind.Stale => RejectReason.StaleOrder,
            OrderCancelResultKind.Conflict => RejectReason.BadRequest,
            OrderCancelResultKind.WalBackpressure => RejectReason.WalBackpressure,
            OrderCancelResultKind.GatewayFailed => RejectReason.GatewayFailed,
            _ => RejectReason.BadRequest,
        };

        _logger.LogInformation(
            "fixp.cancel.reject reason={Reason} kind={Kind} cred={Cred} clOrdId={ClOrdId} orig={Orig}",
            reason, result.Kind, scope.Principal.CredShortId, externalCancelClOrdId, externalOrigClOrdId);

        await WriteExecutionReportRejectAsync(stream,
            externalClOrdId: externalCancelClOrdId,
            origExternalClOrdId: externalOrigClOrdId,
            securityId: securityId,
            side: decoded.Side,
            ordType: 0,
            qty: 0,
            priceMantissa: PriceOptional.MantissaNullValue,
            ordRejReason: reason,
            cxlRejResponseTo: CxlRejResponseTo.CANCEL,
            ct).ConfigureAwait(false);
    }

    private static EndClientId OwnerFor(FixpConnectionScope scope)
    {
        // RFC §4.6: bot-origin orders book ownership under a synthetic
        // "bot:<credShortId>" end-client so they share no namespace with
        // human users. CredShortId is `b3t_<10>` lowercase already.
        return new EndClientId("bot:" + scope.Principal.CredShortId.ToLowerInvariant());
    }

    private static bool TryMapSide(Side raw, out OrderSide side)
    {
        switch (raw)
        {
            case Side.BUY: side = OrderSide.Buy; return true;
            case Side.SELL: side = OrderSide.Sell; return true;
            default: side = default; return false;
        }
    }

    internal readonly record struct FixpOrderHandlingResult(
        bool ShouldKeepSession,
        ulong ReconciliationClOrdId)
    {
        public static FixpOrderHandlingResult Keep { get; } = new(true, 0);

        public static FixpOrderHandlingResult CloseForReconciliation(ulong clOrdId)
        {
            if (clOrdId == 0) throw new ArgumentOutOfRangeException(nameof(clOrdId));
            return new FixpOrderHandlingResult(false, clOrdId);
        }
    }

    internal static bool TryMapOrdType(OrdType raw, out OrderType type)
    {
        switch (raw)
        {
            case OrdType.LIMIT: type = OrderType.Limit; return true;
            case OrdType.MARKET: type = OrderType.Market; return true;
            // Q1.1 (#253). Stop variants and MWL added with the order
            // surface expansion. RLP / PEGGED_MIDPOINT remain unsupported
            // in v0 — the domain has no representation for them yet.
            case OrdType.STOP_LOSS: type = OrderType.StopLoss; return true;
            case OrdType.STOP_LIMIT: type = OrderType.StopLimit; return true;
            case OrdType.MARKET_WITH_LEFTOVER_AS_LIMIT: type = OrderType.MarketWithLeftover; return true;
            default: type = default; return false;
        }
    }

    /// <summary>
    /// Q1.1 (#253). Inbound SBE <c>TimeInForce</c> → Domain mapping.
    /// Rejects unknown wire bytes so a malformed inbound order is a
    /// clean BMR rather than an opaque BadRequest deeper in the
    /// pipeline.
    /// </summary>
    internal static bool TryMapTimeInForce(SbeTif raw, out DomainTif tif)
    {
        switch (raw)
        {
            case SbeTif.DAY: tif = DomainTif.Day; return true;
            case SbeTif.GOOD_TILL_CANCEL: tif = DomainTif.GTC; return true;
            case SbeTif.IMMEDIATE_OR_CANCEL: tif = DomainTif.IOC; return true;
            case SbeTif.FILL_OR_KILL: tif = DomainTif.FOK; return true;
            case SbeTif.GOOD_TILL_DATE: tif = DomainTif.GTD; return true;
            case SbeTif.AT_THE_CLOSE: tif = DomainTif.AtClose; return true;
            case SbeTif.GOOD_FOR_AUCTION: tif = DomainTif.GoodForAuction; return true;
            default: tif = default; return false;
        }
    }

    // ─── Outbound writers ────────────────────────────────────────────────

    private static async Task WriteBusinessMessageRejectAsync(
        Stream stream,
        MessageType refMsgType,
        uint refSeqNum,
        ulong businessRejectRefID,
        uint reason,
        CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(BusinessMessageRejectData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            // Some optional fields on the SBE codegen surface as
            // read-only `Nullable<T>` properties, so we write the wire
            // layout via byte-offset MemoryMarshal calls instead. The
            // outbound business header (offsets 0..17) stays zero.
            Span<byte> body = stackalloc byte[BusinessMessageRejectData.BLOCK_LENGTH];
            body.Clear();
            body[18] = (byte)refMsgType;
            MemoryMarshal.Write(body[20..], in refSeqNum);
            MemoryMarshal.Write(body[24..], in businessRejectRefID);
            MemoryMarshal.Write(body[32..], in reason);

            SofhFrameWriter.WriteFrame(buf.AsSpan(0, frameSize),
                (ushort)BusinessMessageRejectData.BLOCK_LENGTH,
                (ushort)BusinessMessageRejectData.MESSAGE_ID,
                SchemaIdV6, VersionApp,
                body);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task WriteExecutionReportRejectAsync(
        Stream stream,
        ulong externalClOrdId,
        ulong origExternalClOrdId,
        ulong securityId,
        Side side,
        OrdType ordType,
        ulong qty,
        long priceMantissa,
        uint ordRejReason,
        CxlRejResponseTo cxlRejResponseTo,
        CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(ExecutionReport_RejectData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            // See WriteBusinessMessageRejectAsync for rationale on
            // direct byte-offset writes. Offsets here track the SBE v6
            // ExecutionReport_Reject schema (size 164).
            Span<byte> body = stackalloc byte[ExecutionReport_RejectData.BLOCK_LENGTH];
            body.Clear();
            body[18] = (byte)side;
            body[19] = (byte)cxlRejResponseTo;
            MemoryMarshal.Write(body[20..], in externalClOrdId);
            MemoryMarshal.Write(body[36..], in securityId);
            MemoryMarshal.Write(body[44..], in ordRejReason);
            ulong transactTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
            MemoryMarshal.Write(body[48..], in transactTime);
            MemoryMarshal.Write(body[72..], in origExternalClOrdId);
            body[84] = (byte)ordType;
            MemoryMarshal.Write(body[88..], in qty);
            MemoryMarshal.Write(body[96..], in priceMantissa);
            long stopMantissaNull = PriceOptional.MantissaNullValue;
            MemoryMarshal.Write(body[104..], in stopMantissaNull);

            SofhFrameWriter.WriteFrame(buf.AsSpan(0, frameSize),
                (ushort)ExecutionReport_RejectData.BLOCK_LENGTH,
                (ushort)ExecutionReport_RejectData.MESSAGE_ID,
                SchemaIdV6, VersionApp,
                body);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
}
