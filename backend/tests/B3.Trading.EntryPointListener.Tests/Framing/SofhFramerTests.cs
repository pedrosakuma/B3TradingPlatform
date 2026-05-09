using System.Buffers.Binary;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Framing;

namespace B3.Trading.EntryPointListener.Tests.Framing;

public class SofhFramerTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] BuildFrame(ushort blockLength, ushort templateId, ushort schemaId, ushort version, byte[] body)
    {
        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
        SofhFrameWriter.WriteFrame(buf, blockLength, templateId, schemaId, version, body);
        return buf;
    }

    private static byte[] NegotiateBody(uint sessionId = 1, ulong sessionVerId = 1)
    {
        var body = new byte[NegotiateData.BLOCK_LENGTH];
        BinaryPrimitives.WriteUInt32LittleEndian(body, sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), sessionVerId);
        return body;
    }

    private static byte[] TerminateBody(uint sessionId = 1, ulong sessionVerId = 1, byte code = 1)
    {
        var body = new byte[TerminateData.BLOCK_LENGTH];
        BinaryPrimitives.WriteUInt32LittleEndian(body, sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), sessionVerId);
        body[12] = code;
        return body;
    }

    // ─── Roundtrip ────────────────────────────────────────────────────────────

    [Fact]
    public void Negotiate_Roundtrip_WriteThenRead()
    {
        var body = NegotiateBody(sessionId: 42, sessionVerId: 7);
        var frame = BuildFrame(
            (ushort)NegotiateData.BLOCK_LENGTH,
            (ushort)NegotiateData.MESSAGE_ID,
            1, 6, body);

        var reader = new SofhFrameReader();
        reader.Append(frame);

        Assert.True(reader.TryReadFrame(out var f));
        Assert.Equal((ushort)NegotiateData.BLOCK_LENGTH, f.BlockLength);
        Assert.Equal((ushort)NegotiateData.MESSAGE_ID, f.TemplateId);
        Assert.Equal((ushort)1, f.SchemaId);
        Assert.Equal((ushort)6, f.Version);
        Assert.Equal(body.Length, f.Payload.Length);

        var msg = MemoryMarshal.Read<NegotiateData>(f.Payload);
        Assert.Equal((uint)42, (uint)msg.SessionID);
        Assert.Equal((ulong)7, (ulong)msg.SessionVerID);
    }

    [Fact]
    public void Establish_Roundtrip_WriteThenRead()
    {
        var body = new byte[EstablishData.BLOCK_LENGTH];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 99);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), 2);

        var frame = BuildFrame(
            (ushort)EstablishData.BLOCK_LENGTH,
            (ushort)EstablishData.MESSAGE_ID,
            1, 6, body);

        var reader = new SofhFrameReader();
        reader.Append(frame);

        Assert.True(reader.TryReadFrame(out var f));
        Assert.Equal((ushort)EstablishData.MESSAGE_ID, f.TemplateId);
        var msg = MemoryMarshal.Read<EstablishData>(f.Payload);
        Assert.Equal((uint)99, (uint)msg.SessionID);
    }

    [Fact]
    public void Terminate_Roundtrip_WriteThenRead()
    {
        var body = TerminateBody(sessionId: 5, code: (byte)TerminationCode.FINISHED);
        var frame = BuildFrame(
            (ushort)TerminateData.BLOCK_LENGTH,
            (ushort)TerminateData.MESSAGE_ID,
            1, 0, body);

        var reader = new SofhFrameReader();
        reader.Append(frame);

        Assert.True(reader.TryReadFrame(out var f));
        Assert.Equal((ushort)TerminateData.MESSAGE_ID, f.TemplateId);
        var msg = MemoryMarshal.Read<TerminateData>(f.Payload);
        Assert.Equal(TerminationCode.FINISHED, msg.TerminationCode);
    }

    // ─── Multi-frame in single Append ────────────────────────────────────────

    [Fact]
    public void MultiFrameInSingleAppend_BothFramesReturned()
    {
        var body1 = NegotiateBody(sessionId: 1);
        var body2 = NegotiateBody(sessionId: 2);
        var frame1 = BuildFrame((ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID, 1, 6, body1);
        var frame2 = BuildFrame((ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID, 1, 6, body2);

        var combined = frame1.Concat(frame2).ToArray();
        var reader = new SofhFrameReader();
        reader.Append(combined);

        Assert.True(reader.TryReadFrame(out var f1));
        Assert.True(reader.TryReadFrame(out var f2));
        Assert.False(reader.TryReadFrame(out _));

        var m1 = MemoryMarshal.Read<NegotiateData>(f1.Payload);
        var m2 = MemoryMarshal.Read<NegotiateData>(f2.Payload);
        Assert.Equal((uint)1, (uint)m1.SessionID);
        Assert.Equal((uint)2, (uint)m2.SessionID);
    }

    // ─── Partial-frame split across N appends ─────────────────────────────────

    [Fact]
    public void PartialFrame_OneByteAtATime_Reassembled()
    {
        var body = NegotiateBody(sessionId: 77);
        var frame = BuildFrame((ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID, 1, 6, body);

        var reader = new SofhFrameReader();
        for (var i = 0; i < frame.Length - 1; i++)
        {
            reader.Append(frame.AsSpan(i, 1));
            Assert.False(reader.TryReadFrame(out _), $"Should not have a frame yet at byte {i + 1}");
        }

        reader.Append(frame.AsSpan(frame.Length - 1, 1));
        Assert.True(reader.TryReadFrame(out var f));
        var msg = MemoryMarshal.Read<NegotiateData>(f.Payload);
        Assert.Equal((uint)77, (uint)msg.SessionID);
    }

    // ─── Error cases ──────────────────────────────────────────────────────────

    [Fact]
    public void InvalidEncodingType_SetsProtocolError()
    {
        var buf = new byte[SofhFraming.MinFrameSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)SofhFraming.MinFrameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 0xDEAD); // wrong encoding type

        var reader = new SofhFrameReader();
        reader.Append(buf);

        Assert.False(reader.TryReadFrame(out _));
        Assert.True(reader.HasProtocolError);
        Assert.Contains("encodingType", reader.ProtocolErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OversizedFrame_SetsProtocolError()
    {
        var buf = new byte[SofhFraming.MinFrameSize];
        // Write messageLength > MaxFrameSize
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)(SofhFraming.MaxFrameSize + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), SofhFraming.SbeLittleEndianEncodingType);

        var reader = new SofhFrameReader();
        reader.Append(buf);

        Assert.False(reader.TryReadFrame(out _));
        Assert.True(reader.HasProtocolError);
        Assert.Contains("maximum", reader.ProtocolErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroLengthPayload_ValidFrame()
    {
        var frame = BuildFrame(0, 7, 1, 0, Array.Empty<byte>());
        var reader = new SofhFrameReader();
        reader.Append(frame);

        Assert.True(reader.TryReadFrame(out var f));
        Assert.Equal((ushort)0, f.BlockLength);
        Assert.Equal((ushort)7, f.TemplateId);
        Assert.Equal(0, f.Payload.Length);
        Assert.False(reader.HasProtocolError);
    }
}
