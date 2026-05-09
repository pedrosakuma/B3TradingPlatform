using System.Buffers;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Framing;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #172 (F). Encodes a domain <see cref="ExecutionEvent"/> into
/// the matching SBE ExecutionReport variant and wraps it in a SOFH frame.
/// The bot's external ClOrdID — restored from
/// <see cref="IUserBotOrderMappingRegistry"/> — is the only field the
/// bot strictly needs to correlate its order book with the wire message;
/// optional fields not derivable from <see cref="ExecutionEvent"/> are
/// left at their SBE null sentinels.
///
/// <para>v0 covers the five execution kinds the platform actually
/// surfaces today: <c>New</c>, <c>PartialFill</c>, <c>Fill</c>,
/// <c>Canceled</c>, <c>Rejected</c>. <c>Replaced</c> uses the same
/// <c>ExecutionReport_Modify</c> path. Any future ER kind that doesn't
/// map cleanly throws a guarded <see cref="NotSupportedException"/> so
/// the bug surfaces loudly rather than as silent ER loss.</para>
///
/// <para>Field-offset writes mirror the pattern already used by
/// <c>FixpOrderAdapter.WriteExecutionReportRejectAsync</c> (some SBE
/// optionals expose only <c>Nullable&lt;T&gt;</c> getters and require
/// raw memory writes to set the wire layout).</para>
/// </summary>
internal static class OutboundExecutionReportEncoder
{
    private const ushort SchemaIdV6 = 1;
    private const ushort VersionApp = 6;

    /// <summary>
    /// Encodes a SOFH-framed SBE ExecutionReport for the given
    /// <paramref name="ev"/> + bot mapping. Returns a heap-allocated
    /// byte array sized exactly to the frame (no slack). The buffer is
    /// what the multiplexer hands to <c>BotOutboundBuffer.Append</c> and
    /// to <c>IBotSessionOutboundSender.TryEnqueue</c> simultaneously.
    /// </summary>
    public static byte[] Encode(
        ExecutionEvent ev,
        ulong externalClOrdId,
        ulong externalOrigClOrdId)
    {
        return ev.Kind switch
        {
            ExecKind.New => EncodeNew(ev, externalClOrdId),
            ExecKind.PartialFill or ExecKind.Fill => EncodeTrade(ev, externalClOrdId),
            ExecKind.Canceled => EncodeCancel(ev, externalClOrdId, externalOrigClOrdId),
            ExecKind.Replaced => EncodeModify(ev, externalClOrdId, externalOrigClOrdId),
            ExecKind.Rejected => EncodeReject(ev, externalClOrdId, externalOrigClOrdId),
            _ => throw new NotSupportedException(
                $"ExecKind {ev.Kind} has no SBE ExecutionReport mapping in v0."),
        };
    }

    private static byte[] EncodeNew(ExecutionEvent ev, ulong externalClOrdId)
    {
        Span<byte> body = stackalloc byte[ExecutionReport_NewData.BLOCK_LENGTH];
        body.Clear();
        body[18] = (byte)ToSbeSide(ev.Side);
        body[19] = (byte)ToSbeOrdStatus(ev.Status);
        MemoryMarshal.Write(body[20..], in externalClOrdId);
        ulong transactTime = NanosNow();
        MemoryMarshal.Write(body[64..], in transactTime);

        return Frame(
            (ushort)ExecutionReport_NewData.BLOCK_LENGTH,
            (ushort)ExecutionReport_NewData.MESSAGE_ID,
            body);
    }

    private static byte[] EncodeTrade(ExecutionEvent ev, ulong externalClOrdId)
    {
        Span<byte> body = stackalloc byte[ExecutionReport_TradeData.BLOCK_LENGTH];
        body.Clear();
        body[18] = (byte)ToSbeSide(ev.Side);
        body[19] = (byte)ToSbeOrdStatus(ev.Status);
        MemoryMarshal.Write(body[20..], in externalClOrdId);
        ulong lastQty = (ulong)Math.Max(0, ev.LastQuantity);
        MemoryMarshal.Write(body[48..], in lastQty);
        long lastPxMantissa = ToPriceMantissa(ev.LastPrice);
        MemoryMarshal.Write(body[56..], in lastPxMantissa);
        ulong transactTime = NanosNow();
        MemoryMarshal.Write(body[72..], in transactTime);
        ulong leavesQty = (ulong)Math.Max(0, ev.LeavesQuantity);
        MemoryMarshal.Write(body[80..], in leavesQty);
        ulong cumQty = (ulong)Math.Max(0, ev.CumulativeQuantity);
        MemoryMarshal.Write(body[88..], in cumQty);

        return Frame(
            (ushort)ExecutionReport_TradeData.BLOCK_LENGTH,
            (ushort)ExecutionReport_TradeData.MESSAGE_ID,
            body);
    }

    private static byte[] EncodeCancel(ExecutionEvent ev, ulong externalClOrdId, ulong externalOrigClOrdId)
    {
        Span<byte> body = stackalloc byte[ExecutionReport_CancelData.BLOCK_LENGTH];
        body.Clear();
        body[18] = (byte)ToSbeSide(ev.Side);
        body[19] = (byte)ToSbeOrdStatus(ev.Status);
        MemoryMarshal.Write(body[20..], in externalClOrdId);
        ulong cumQty = (ulong)Math.Max(0, ev.CumulativeQuantity);
        MemoryMarshal.Write(body[44..], in cumQty);
        ulong transactTime = NanosNow();
        MemoryMarshal.Write(body[64..], in transactTime);
        // OrigClOrdID is at offset 88 per the SBE schema. When the multiplexer
        // could not resolve a separate cancel-side mapping (forward-route
        // scenarios), we fall back to 0 (= SBE null sentinel for this field).
        MemoryMarshal.Write(body[88..], in externalOrigClOrdId);

        return Frame(
            (ushort)ExecutionReport_CancelData.BLOCK_LENGTH,
            (ushort)ExecutionReport_CancelData.MESSAGE_ID,
            body);
    }

    private static byte[] EncodeModify(ExecutionEvent ev, ulong externalClOrdId, ulong externalOrigClOrdId)
    {
        Span<byte> body = stackalloc byte[ExecutionReport_ModifyData.BLOCK_LENGTH];
        body.Clear();
        body[18] = (byte)ToSbeSide(ev.Side);
        body[19] = (byte)ToSbeOrdStatus(ev.Status);
        MemoryMarshal.Write(body[20..], in externalClOrdId);
        ulong leavesQty = (ulong)Math.Max(0, ev.LeavesQuantity);
        MemoryMarshal.Write(body[44..], in leavesQty);
        ulong transactTime = NanosNow();
        MemoryMarshal.Write(body[64..], in transactTime);
        ulong cumQty = (ulong)Math.Max(0, ev.CumulativeQuantity);
        MemoryMarshal.Write(body[72..], in cumQty);
        // OrigClOrdID offset 96 on the Modify body.
        MemoryMarshal.Write(body[96..], in externalOrigClOrdId);

        return Frame(
            (ushort)ExecutionReport_ModifyData.BLOCK_LENGTH,
            (ushort)ExecutionReport_ModifyData.MESSAGE_ID,
            body);
    }

    private static byte[] EncodeReject(ExecutionEvent ev, ulong externalClOrdId, ulong externalOrigClOrdId)
    {
        Span<byte> body = stackalloc byte[ExecutionReport_RejectData.BLOCK_LENGTH];
        body.Clear();
        body[18] = (byte)ToSbeSide(ev.Side);
        // body[19] is CxlRejResponseTo on Reject (no OrdStatus field — the
        // schema constant ORD_STATUS = REJECTED carries the meaning).
        // Leave it at 0 (= ORDER) for now; cancel/modify rejects are a
        // dedicated path on the inbound adapter, see FixpOrderAdapter.
        MemoryMarshal.Write(body[20..], in externalClOrdId);
        ulong transactTime = NanosNow();
        MemoryMarshal.Write(body[48..], in transactTime);
        // OrigClOrdID at offset 72 (optional).
        MemoryMarshal.Write(body[72..], in externalOrigClOrdId);

        return Frame(
            (ushort)ExecutionReport_RejectData.BLOCK_LENGTH,
            (ushort)ExecutionReport_RejectData.MESSAGE_ID,
            body);
    }

    private static byte[] Frame(ushort blockLength, ushort templateId, ReadOnlySpan<byte> body)
    {
        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
        SofhFrameWriter.WriteFrame(buf, blockLength, templateId, SchemaIdV6, VersionApp, body);
        return buf;
    }

    private static Side ToSbeSide(OrderSide side) => side switch
    {
        OrderSide.Buy => Side.BUY,
        OrderSide.Sell => Side.SELL,
        _ => Side.BUY,
    };

    private static OrdStatus ToSbeOrdStatus(OrderStatus status) => status switch
    {
        OrderStatus.PendingNew => OrdStatus.NEW,
        OrderStatus.Working => OrdStatus.NEW,
        OrderStatus.PartiallyFilled => OrdStatus.PARTIALLY_FILLED,
        OrderStatus.Filled => OrdStatus.FILLED,
        OrderStatus.Cancelled => OrdStatus.CANCELED,
        OrderStatus.Rejected => OrdStatus.REJECTED,
        OrderStatus.Replaced => OrdStatus.REPLACED,
        _ => OrdStatus.NEW,
    };

    private static long ToPriceMantissa(decimal price)
    {
        // B3 Price scale = 8 decimals (per SBE schema's Price composite).
        // ExecutionEvent carries a decimal; convert to int64 mantissa.
        // Saturate on overflow rather than throwing — the encoded ER must
        // not lose the bot's correlation just because one fill price
        // exceeded the scaled range.
        try
        {
            return (long)(price * 1_000_000_00m);
        }
        catch (OverflowException)
        {
            return price > 0 ? long.MaxValue : long.MinValue;
        }
    }

    private static ulong NanosNow()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
}
