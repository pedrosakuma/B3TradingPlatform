using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Framing;
using Microsoft.Extensions.Logging;

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
/// to write the synchronous transport-level reject paths.</para>
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
    /// <c>true</c> to keep the connection alive; the only failures that
    /// terminate the session are framing-level (those are handled by the
    /// caller, not here).
    /// </summary>
    public async Task HandleNewOrderSingleAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> payload,
        FixpConnectionScope scope,
        CancellationToken ct)
    {
        if (payload.Length < NewOrderSingleData.BLOCK_LENGTH)
        {
            await WriteBusinessMessageRejectAsync(stream,
                refMsgType: MessageType.NewOrderSingle,
                refSeqNum: 0, businessRejectRefID: 0,
                reason: RejectReason.InvalidShape, ct).ConfigureAwait(false);
            return;
        }

        var msg = MemoryMarshal.Read<NewOrderSingleData>(payload.Span);
        var externalClOrdId = (ulong)msg.ClOrdID;
        var securityId = (ulong)msg.SecurityID;
        var refSeqNum = (uint)msg.BusinessHeader.MsgSeqNum;

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
            return;
        }

        // 2. Validate side/ordType up-front so a malformed wire byte is
        //    a clean BMR rather than a generic BadRequest from the
        //    pipeline. Only the LIMIT/MARKET subset is supported by the
        //    domain model in v0; everything else is rejected.
        if (!TryMapSide(msg.Side, out var side) || !TryMapOrdType(msg.OrdType, out var type))
        {
            await WriteBusinessMessageRejectAsync(stream,
                MessageType.NewOrderSingle, refSeqNum, externalClOrdId,
                RejectReason.InvalidShape, ct).ConfigureAwait(false);
            return;
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
            return;
        }

        var owner = OwnerFor(scope);
        var qty = (long)(ulong)msg.OrderQty;
        decimal? price = msg.Price.Mantissa is { } m
            ? m / (decimal)PriceOptional.Multiplier
            : null;

        var req = new OrderSubmissionRequest(
            Owner: owner,
            FirmId: "default",
            Symbol: symbol,
            SecurityId: securityId,
            Side: side,
            Type: type,
            Quantity: qty,
            Price: price)
        {
            BotOrigin = new BotOrigin(scope.Principal.CredentialId, externalClOrdId),
        };

        var result = await _submit.SubmitAsync(req, ct).ConfigureAwait(false);
        if (result.Kind == OrderSubmissionResultKind.Accepted) return;

        // RFC §4.7 — submit-time rejections produce a synthetic
        // ExecutionReport_Reject so the bot sees the same shape it would
        // have seen for a venue-side reject (consistent ER stream).
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
            side: msg.Side,
            ordType: msg.OrdType,
            qty: (ulong)msg.OrderQty,
            priceMantissa: msg.Price.Mantissa ?? PriceOptional.MantissaNullValue,
            ordRejReason: reason,
            cxlRejResponseTo: CxlRejResponseTo.NEW,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decode an inbound <c>OrderCancelRequest</c> (id=105) and dispatch
    /// it through <see cref="OrderCancelService.CancelAsync"/> after
    /// resolving the bot's external <c>OrigClOrdID</c> back to the
    /// platform's internal id via the side-mapping registry.
    /// </summary>
    public async Task HandleOrderCancelRequestAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> payload,
        FixpConnectionScope scope,
        CancellationToken ct)
    {
        if (payload.Length < OrderCancelRequestData.BLOCK_LENGTH)
        {
            await WriteBusinessMessageRejectAsync(stream,
                MessageType.OrderCancelRequest, 0, 0,
                RejectReason.InvalidShape, ct).ConfigureAwait(false);
            return;
        }

        var msg = MemoryMarshal.Read<OrderCancelRequestData>(payload.Span);
        var externalCancelClOrdId = (ulong)msg.ClOrdID;
        var externalOrigClOrdId = msg.OrigClOrdID.GetValueOrDefault();
        var securityId = (ulong)msg.SecurityID;
        var refSeqNum = (uint)msg.BusinessHeader.MsgSeqNum;

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
            side: msg.Side,
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

    private static bool TryMapOrdType(OrdType raw, out OrderType type)
    {
        switch (raw)
        {
            case OrdType.LIMIT: type = OrderType.Limit; return true;
            case OrdType.MARKET: type = OrderType.Market; return true;
            default: type = default; return false;
        }
    }

    // ─── Outbound writers ────────────────────────────────────────────────

    private static async Task WriteBusinessMessageRejectAsync(
        NetworkStream stream,
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
        NetworkStream stream,
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
