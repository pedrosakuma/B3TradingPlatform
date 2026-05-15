using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// RFC §5.6 (P10/F6). Zero-allocation SBE decoders for the inbound hot
/// message types: <c>NewOrderSingle</c>, <c>OrderCancelRequest</c>, and
/// <c>Sequence</c>. Each <c>TryDecode*</c> reads the SBE block layout
/// directly from a <see cref="ReadOnlySpan{T}"/> over the framer's
/// rotating buffer (no per-frame <c>byte[]</c> allocation) and copies
/// only the value-type fields the dispatcher actually needs into a
/// stack-resident <c>Decoded*</c> struct.
///
/// <para>The decoded structs intentionally mirror — but do not extend —
/// the SBE codegen layout. They contain only blittable value types so
/// they survive across the dispatcher's <c>await</c> boundary without
/// keeping any reference into the receive buffer.</para>
///
/// <para>Cold message types (Negotiate, Establish, Terminate,
/// RetransmitRequest) continue to flow through the legacy heap-copy
/// path in <see cref="FixpSessionConnection"/> — they are infrequent
/// and not worth the additional surface area.</para>
/// </summary>
internal static class InboundDecoders
{
    public static bool TryDecodeNewOrderSingle(
        ReadOnlySpan<byte> payload, out DecodedNewOrderSingle decoded)
    {
        if (payload.Length < NewOrderSingleData.BLOCK_LENGTH)
        {
            decoded = default;
            return false;
        }

        var msg = MemoryMarshal.Read<NewOrderSingleData>(payload);
        decoded = new DecodedNewOrderSingle
        {
            MsgSeqNum = (uint)msg.BusinessHeader.MsgSeqNum,
            ClOrdId = (ulong)msg.ClOrdID,
            SecurityId = (ulong)msg.SecurityID,
            Side = msg.Side,
            OrdType = msg.OrdType,
            OrderQty = (ulong)msg.OrderQty,
            PriceMantissa = msg.Price.Mantissa,
            // Q1.1 (#253) — additional surface fields plumbed for
            // Stop/StopLimit + GTD + non-Day TIF support.
            TimeInForce = msg.TimeInForce,
            StopPxMantissa = msg.StopPx.Mantissa,
            ExpireDateRaw = msg.ExpireDate ?? 0,
        };
        return true;
    }

    public static bool TryDecodeOrderCancelRequest(
        ReadOnlySpan<byte> payload, out DecodedOrderCancelRequest decoded)
    {
        if (payload.Length < OrderCancelRequestData.BLOCK_LENGTH)
        {
            decoded = default;
            return false;
        }

        var msg = MemoryMarshal.Read<OrderCancelRequestData>(payload);
        decoded = new DecodedOrderCancelRequest
        {
            MsgSeqNum = (uint)msg.BusinessHeader.MsgSeqNum,
            ClOrdId = (ulong)msg.ClOrdID,
            OrigClOrdId = msg.OrigClOrdID.GetValueOrDefault(),
            SecurityId = (ulong)msg.SecurityID,
            Side = msg.Side,
        };
        return true;
    }

    public static bool TryDecodeSequence(
        ReadOnlySpan<byte> payload, out DecodedSequence decoded)
    {
        if (payload.Length < SequenceData.BLOCK_LENGTH)
        {
            decoded = default;
            return false;
        }

        var msg = MemoryMarshal.Read<SequenceData>(payload);
        decoded = new DecodedSequence
        {
            NextSeqNo = (ulong)msg.NextSeqNo,
        };
        return true;
    }
}

/// <summary>
/// Stack-resident projection of the SBE <c>NewOrderSingle</c> fields
/// the dispatcher and <see cref="FixpOrderAdapter"/> consume.
/// </summary>
internal readonly struct DecodedNewOrderSingle
{
    public uint MsgSeqNum { get; init; }
    public ulong ClOrdId { get; init; }
    public ulong SecurityId { get; init; }
    public Side Side { get; init; }
    public OrdType OrdType { get; init; }
    public ulong OrderQty { get; init; }
    public long? PriceMantissa { get; init; }

    /// <summary>Q1.1 (#253). SBE wire byte for time-in-force.</summary>
    public TimeInForce TimeInForce { get; init; }

    /// <summary>
    /// Q1.1 (#253). PriceOptional mantissa for the stop trigger; null
    /// when the wire field was the SBE null sentinel. Same scaling as
    /// <see cref="PriceMantissa"/>.
    /// </summary>
    public long? StopPxMantissa { get; init; }

    /// <summary>
    /// Q1.1 (#253). Raw SBE <c>ExpireDate</c> (<c>ushort</c>, 0 = null).
    /// Encoded as days since 1970-01-01 by B3 convention. Decoded into a
    /// <see cref="DateTimeOffset"/> at the dispatch boundary.
    /// </summary>
    public ushort ExpireDateRaw { get; init; }
}

/// <summary>
/// Stack-resident projection of the SBE <c>OrderCancelRequest</c>
/// fields the dispatcher and <see cref="FixpOrderAdapter"/> consume.
/// </summary>
internal readonly struct DecodedOrderCancelRequest
{
    public uint MsgSeqNum { get; init; }
    public ulong ClOrdId { get; init; }
    public ulong OrigClOrdId { get; init; }
    public ulong SecurityId { get; init; }
    public Side Side { get; init; }
}

/// <summary>
/// Stack-resident projection of the SBE <c>Sequence</c> field the
/// session uses to advance its inbound watermark.
/// </summary>
internal readonly struct DecodedSequence
{
    public ulong NextSeqNo { get; init; }
}
