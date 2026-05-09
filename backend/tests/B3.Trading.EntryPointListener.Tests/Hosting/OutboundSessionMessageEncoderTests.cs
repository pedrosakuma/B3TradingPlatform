using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// Sub-issue #173 (G). Encoder tests assert wire-shape conformance for
/// the four session-control messages the listener emits post-Establish:
/// Sequence, NotApplied, Retransmission, RetransmitReject.
///
/// <para>Each test rebuilds the encoded frame, parses it through the
/// SOFH framer + the matching SBE reader, and asserts the round-tripped
/// fields. This is the same pattern B/D/E/F use for Negotiate/Establish.</para>
/// </summary>
public class OutboundSessionMessageEncoderTests
{
    private static (ushort templateId, byte[] payload) ReadOneFrame(byte[] frame)
    {
        var reader = new SofhFrameReader();
        reader.Append(frame);
        Assert.True(reader.TryReadFrame(out var f));
        return (f.TemplateId, f.Payload.ToArray());
    }

    [Fact]
    public void EncodeSequence_RoundTrips_NextSeqNo()
    {
        var bytes = OutboundSessionMessageEncoder.EncodeSequence(nextSeqNo: 42);
        var (id, payload) = ReadOneFrame(bytes);
        Assert.Equal((ushort)SequenceData.MESSAGE_ID, id);
        var msg = MemoryMarshal.Read<SequenceData>(payload);
        Assert.Equal(42UL, (ulong)msg.NextSeqNo);
    }

    [Fact]
    public void EncodeNotApplied_RoundTrips_FromAndCount()
    {
        var bytes = OutboundSessionMessageEncoder.EncodeNotApplied(fromSeqNo: 10, count: 5);
        var (id, payload) = ReadOneFrame(bytes);
        Assert.Equal((ushort)NotAppliedData.MESSAGE_ID, id);
        var msg = MemoryMarshal.Read<NotAppliedData>(payload);
        Assert.Equal(10UL, (ulong)msg.FromSeqNo);
        Assert.Equal(5U, (uint)msg.Count);
    }

    [Fact]
    public void EncodeRetransmission_RoundTrips_AllFields()
    {
        var bytes = OutboundSessionMessageEncoder.EncodeRetransmission(
            sessionId: 7, requestTimestampNanos: 1_700_000_000_000UL, nextSeqNo: 100, count: 3);
        var (id, payload) = ReadOneFrame(bytes);
        Assert.Equal((ushort)RetransmissionData.MESSAGE_ID, id);
        var msg = MemoryMarshal.Read<RetransmissionData>(payload);
        Assert.Equal(7U, (uint)msg.SessionID);
        Assert.Equal(1_700_000_000_000UL, msg.RequestTimestamp.Time);
        Assert.Equal(100UL, (ulong)msg.NextSeqNo);
        Assert.Equal(3U, (uint)msg.Count);
    }

    [Theory]
    [InlineData(RetransmitRejectCode.OUT_OF_RANGE)]
    [InlineData(RetransmitRejectCode.INVALID_FROMSEQNO)]
    [InlineData(RetransmitRejectCode.INVALID_COUNT)]
    [InlineData(RetransmitRejectCode.INVALID_SESSION)]
    public void EncodeRetransmitReject_RoundTrips_Code(RetransmitRejectCode code)
    {
        var bytes = OutboundSessionMessageEncoder.EncodeRetransmitReject(
            sessionId: 9, requestTimestampNanos: 12345UL, code);
        var (id, payload) = ReadOneFrame(bytes);
        Assert.Equal((ushort)RetransmitRejectData.MESSAGE_ID, id);
        var msg = MemoryMarshal.Read<RetransmitRejectData>(payload);
        Assert.Equal(9U, (uint)msg.SessionID);
        Assert.Equal(12345UL, msg.RequestTimestamp.Time);
        Assert.Equal(code, msg.RetransmitRejectCode);
    }
}
