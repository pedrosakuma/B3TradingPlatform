using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// RFC §5.6 (P10/F6). Round-trip equivalence tests for the zero-copy
/// inbound SBE decoders. The acceptance criterion the production
/// adapter cares about is "the zero-copy path produces the same field
/// values the legacy <c>MemoryMarshal.Read&lt;T&gt;</c> path would have
/// produced". So for each hot template we:
///
/// <list type="number">
///   <item>Build a wire-shape buffer using the SBE codegen setters
///     (and raw <c>MemoryMarshal.Write</c> for the read-only optional
///     fields — backing fields are stable across the schema's
///     <c>BLOCK_LENGTH</c>).</item>
///   <item>Decode it through <see cref="InboundDecoders"/>.</item>
///   <item>Decode the *same* buffer through
///     <c>MemoryMarshal.Read&lt;T&gt;</c> — the legacy path the
///     adapter used pre-#204.</item>
///   <item>Assert every projected field on the
///     <see cref="DecodedNewOrderSingle"/>/<see cref="DecodedOrderCancelRequest"/>/<see cref="DecodedSequence"/>
///     struct equals the legacy read.</item>
/// </list>
///
/// <para>This guarantees no behavioural drift between the two paths
/// for the dispatch fields the adapter actually consumes.</para>
/// </summary>
public class InboundDecodersTests
{
    private static byte[] BuildNewOrderSingleBody(
        uint msgSeqNum,
        ulong clOrdId,
        ulong securityId,
        Side side,
        OrdType ordType,
        ulong orderQty,
        long? priceMantissa)
    {
        var body = new byte[NewOrderSingleData.BLOCK_LENGTH];
        var span = body.AsSpan();
        ref var msg = ref MemoryMarshal.AsRef<NewOrderSingleData>(span);
        msg.BusinessHeader = new InboundBusinessHeader
        {
            SessionID = (SessionID)0u,
            MsgSeqNum = (SeqNum)msgSeqNum,
        };
        msg.ClOrdID = (ClOrdID)clOrdId;
        msg.SecurityID = (SecurityID)securityId;
        msg.Side = side;
        msg.OrdType = ordType;
        msg.OrderQty = (Quantity)orderQty;
        // Price.Mantissa has no public setter — write the underlying
        // SBE Int64 backing field directly. The PriceOptional layout is
        // a single Int64 located at the SBE-defined offset within the
        // block; the framework's null sentinel is PriceOptional.MantissaNullValue.
        var priceOffset = GetPriceOffset();
        var mantissa = priceMantissa ?? PriceOptional.MantissaNullValue;
        var mantissaLocal = mantissa; MemoryMarshal.Write(span[priceOffset..], in mantissaLocal);
        return body;
    }

    private static int GetPriceOffset()
    {
        // Discover the Price field offset by writing a sentinel via the
        // raw byte buffer and searching the encoded body for it. We use
        // a value that is guaranteed not to collide with the rest of
        // the zeroed block.
        var probe = new byte[NewOrderSingleData.BLOCK_LENGTH];
        long sentinel = 0x7E5701234ABCDEF0L;
        // Write sentinel at every long-aligned offset until the round-trip
        // through MemoryMarshal.Read<NewOrderSingleData>().Price.Mantissa
        // surfaces it; that offset is the Price.mantissa backing field.
        for (var off = 0; off + sizeof(long) <= probe.Length; off += 1)
        {
            probe.AsSpan().Clear();
            MemoryMarshal.Write(probe.AsSpan(off), in sentinel);
            var read = MemoryMarshal.Read<NewOrderSingleData>(probe);
            if (read.Price.Mantissa == sentinel) return off;
        }
        throw new InvalidOperationException("Could not locate Price field offset in NewOrderSingleData layout.");
    }

    [Fact]
    public void DecodeNewOrderSingle_MatchesLegacyMemoryMarshalRead()
    {
        var body = BuildNewOrderSingleBody(
            msgSeqNum: 7u,
            clOrdId: 0xDEADBEEFCAFEBABEUL,
            securityId: 1234567UL,
            side: Side.SELL,
            ordType: OrdType.LIMIT,
            orderQty: 500UL,
            priceMantissa: 12_345L);

        Assert.True(InboundDecoders.TryDecodeNewOrderSingle(body, out var decoded));

        var legacy = MemoryMarshal.Read<NewOrderSingleData>(body);
        Assert.Equal((uint)legacy.BusinessHeader.MsgSeqNum, decoded.MsgSeqNum);
        Assert.Equal((ulong)legacy.ClOrdID, decoded.ClOrdId);
        Assert.Equal((ulong)legacy.SecurityID, decoded.SecurityId);
        Assert.Equal(legacy.Side, decoded.Side);
        Assert.Equal(legacy.OrdType, decoded.OrdType);
        Assert.Equal((ulong)legacy.OrderQty, decoded.OrderQty);
        Assert.Equal(legacy.Price.Mantissa, decoded.PriceMantissa);
    }

    [Fact]
    public void DecodeNewOrderSingle_PreservesNullPrice_ForMarketOrder()
    {
        var body = BuildNewOrderSingleBody(
            msgSeqNum: 1u,
            clOrdId: 1UL,
            securityId: 1UL,
            side: Side.BUY,
            ordType: OrdType.MARKET,
            orderQty: 10UL,
            priceMantissa: null);

        Assert.True(InboundDecoders.TryDecodeNewOrderSingle(body, out var decoded));
        Assert.Null(decoded.PriceMantissa);
        // And the legacy reader must agree.
        var legacy = MemoryMarshal.Read<NewOrderSingleData>(body);
        Assert.Equal(legacy.Price.Mantissa, decoded.PriceMantissa);
    }

    [Fact]
    public void DecodeNewOrderSingle_ShortPayload_ReturnsFalse()
    {
        Assert.False(InboundDecoders.TryDecodeNewOrderSingle(
            new byte[NewOrderSingleData.BLOCK_LENGTH - 1], out var decoded));
        Assert.Equal(0u, decoded.MsgSeqNum);
        Assert.Equal(0UL, decoded.ClOrdId);
    }

    [Fact]
    public void DecodeOrderCancelRequest_MatchesLegacyMemoryMarshalRead()
    {
        var body = new byte[OrderCancelRequestData.BLOCK_LENGTH];
        var span = body.AsSpan();
        ref var msg = ref MemoryMarshal.AsRef<OrderCancelRequestData>(span);
        msg.BusinessHeader = new InboundBusinessHeader { MsgSeqNum = (SeqNum)4u };
        msg.ClOrdID = (ClOrdID)0xAAAAAAAAUL;
        msg.SecurityID = (SecurityID)777UL;
        msg.Side = Side.SELL;
        // OrigClOrdID has no public setter — write the raw UInt64 backing
        // field. We discover the offset the same way as for Price.
        var origOffset = GetOrigClOrdIdOffset();
        ulong origValue = 0xBBBBBBBBUL;
        MemoryMarshal.Write(span[origOffset..], in origValue);

        Assert.True(InboundDecoders.TryDecodeOrderCancelRequest(body, out var decoded));
        var legacy = MemoryMarshal.Read<OrderCancelRequestData>(body);
        Assert.Equal((uint)legacy.BusinessHeader.MsgSeqNum, decoded.MsgSeqNum);
        Assert.Equal((ulong)legacy.ClOrdID, decoded.ClOrdId);
        Assert.Equal(legacy.OrigClOrdID.GetValueOrDefault(), decoded.OrigClOrdId);
        Assert.Equal((ulong)legacy.SecurityID, decoded.SecurityId);
        Assert.Equal(legacy.Side, decoded.Side);
        Assert.Equal(0xBBBBBBBBUL, decoded.OrigClOrdId);
    }

    private static int GetOrigClOrdIdOffset()
    {
        var probe = new byte[OrderCancelRequestData.BLOCK_LENGTH];
        ulong sentinel = 0x7E57_0123_4ABC_DEF0UL;
        for (var off = 0; off + sizeof(ulong) <= probe.Length; off += 1)
        {
            probe.AsSpan().Clear();
            MemoryMarshal.Write(probe.AsSpan(off), in sentinel);
            var read = MemoryMarshal.Read<OrderCancelRequestData>(probe);
            if (read.OrigClOrdID.GetValueOrDefault() == sentinel) return off;
        }
        throw new InvalidOperationException("Could not locate OrigClOrdID field offset in OrderCancelRequestData layout.");
    }

    [Fact]
    public void DecodeOrderCancelRequest_NullOrigClOrdId_DefaultsToZero()
    {
        // Write the SBE null sentinel directly. GetValueOrDefault() on a
        // null Nullable<ClOrdID> returns 0 — the dispatcher relies on
        // this so the bot mapping registry's TryGetByExternal lookup
        // misses cleanly with an UnknownOrder reject.
        var body = new byte[OrderCancelRequestData.BLOCK_LENGTH];
        var span = body.AsSpan();
        ref var msg = ref MemoryMarshal.AsRef<OrderCancelRequestData>(span);
        msg.BusinessHeader = new InboundBusinessHeader { MsgSeqNum = (SeqNum)1u };
        msg.ClOrdID = (ClOrdID)1UL;
        msg.SecurityID = (SecurityID)1UL;
        msg.Side = Side.BUY;
        var origOffset = GetOrigClOrdIdOffset();
        var nullSentinel = OrderCancelRequestData.OrigClOrdIDNullValue;
        var nullSentinelLocal = nullSentinel; MemoryMarshal.Write(span[origOffset..], in nullSentinelLocal);

        Assert.True(InboundDecoders.TryDecodeOrderCancelRequest(body, out var decoded));
        Assert.Equal(0UL, decoded.OrigClOrdId);
        // Legacy parity:
        var legacy = MemoryMarshal.Read<OrderCancelRequestData>(body);
        Assert.Equal(legacy.OrigClOrdID.GetValueOrDefault(), decoded.OrigClOrdId);
    }

    [Fact]
    public void DecodeOrderCancelRequest_ShortPayload_ReturnsFalse()
    {
        Assert.False(InboundDecoders.TryDecodeOrderCancelRequest(
            new byte[OrderCancelRequestData.BLOCK_LENGTH - 1], out _));
    }

    [Fact]
    public void DecodeSequence_MatchesLegacyMemoryMarshalRead()
    {
        var body = new byte[SequenceData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<SequenceData>(body.AsSpan());
        msg.NextSeqNo = (SeqNum)0xCAFEBABEU;

        Assert.True(InboundDecoders.TryDecodeSequence(body, out var decoded));
        var legacy = MemoryMarshal.Read<SequenceData>(body);
        Assert.Equal((ulong)legacy.NextSeqNo, decoded.NextSeqNo);
        Assert.Equal(0xCAFEBABEUL, decoded.NextSeqNo);
    }

    [Fact]
    public void DecodeSequence_ShortPayload_ReturnsFalse()
    {
        Assert.False(InboundDecoders.TryDecodeSequence(
            new byte[SequenceData.BLOCK_LENGTH - 1], out _));
    }

    [Fact]
    public void DecodeHotTypes_DoNotAllocate_OnSuccessfulPath()
    {
        // RFC §5.6 acceptance: zero per-message heap allocations in the
        // inbound decode path. We measure the per-thread allocation
        // delta across a tight loop of decodes; the only allocations
        // permitted on this path are the up-front buffer fixtures
        // (excluded from the measurement window).
        var nos = BuildNewOrderSingleBody(
            msgSeqNum: 1u, clOrdId: 1UL, securityId: 1UL,
            side: Side.BUY, ordType: OrdType.LIMIT,
            orderQty: 1UL, priceMantissa: 1L);

        var ocr = new byte[OrderCancelRequestData.BLOCK_LENGTH];
        ref var ocrMsg = ref MemoryMarshal.AsRef<OrderCancelRequestData>(ocr.AsSpan());
        ocrMsg.BusinessHeader = new InboundBusinessHeader { MsgSeqNum = (SeqNum)1u };
        ocrMsg.ClOrdID = (ClOrdID)1UL;
        ocrMsg.SecurityID = (SecurityID)1UL;

        var seq = new byte[SequenceData.BLOCK_LENGTH];
        ref var seqMsg = ref MemoryMarshal.AsRef<SequenceData>(seq.AsSpan());
        seqMsg.NextSeqNo = (SeqNum)1U;

        // Warm up + JIT.
        for (var i = 0; i < 16; i++)
        {
            InboundDecoders.TryDecodeNewOrderSingle(nos, out _);
            InboundDecoders.TryDecodeOrderCancelRequest(ocr, out _);
            InboundDecoders.TryDecodeSequence(seq, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            InboundDecoders.TryDecodeNewOrderSingle(nos, out _);
            InboundDecoders.TryDecodeOrderCancelRequest(ocr, out _);
            InboundDecoders.TryDecodeSequence(seq, out _);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }
}
