using System.Buffers.Binary;
using B3.Trading.EntryPointListener.Framing;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

/// <summary>
/// Hostile-frame / fuzz coverage for the SOFH frame reassembler (#534).
/// The public surface faces an arbitrary internet; the reader must NEVER
/// throw on adversarial bytes — it flags <see cref="SofhFrameReader.HasProtocolError"/>
/// (caller terminates) or simply waits for more data. No exception is an
/// acceptable outcome.
/// </summary>
public class HostileFrameFuzzTests
{
    private const ushort SbeEncoding = 0xEB50;
    private const int MinFrame = 12;   // SofhHeaderSize(4) + SbeMessageHeaderSize(8)
    private const int MaxFrame = 16_384;

    private static byte[] Frame(ushort messageLength, ushort encoding, int totalBytes)
    {
        var buf = new byte[Math.Max(totalBytes, MinFrame)];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, messageLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), encoding);
        return buf;
    }

    [Fact]
    public void RandomGarbage_NeverThrows_FlagsErrorOrWaits()
    {
        var rng = new Random(0xB3F);
        for (var i = 0; i < 2000; i++)
        {
            var len = rng.Next(0, 512);
            var data = new byte[len];
            rng.NextBytes(data);

            var reader = new SofhFrameReader();
            // Must not throw regardless of input.
            reader.Append(data);
            while (reader.TryReadFrame(out _)) { }
            // Either consumed cleanly, errored, or is waiting — all fine.
        }
    }

    [Fact]
    public void InvalidEncodingType_FlagsProtocolError()
    {
        var reader = new SofhFrameReader();
        reader.Append(Frame(MinFrame, encoding: 0x1234, totalBytes: MinFrame));
        Assert.False(reader.TryReadFrame(out _));
        Assert.True(reader.HasProtocolError);
        Assert.Contains("encodingType", reader.ProtocolErrorMessage);
    }

    [Fact]
    public void MessageLengthBelowMinimum_FlagsProtocolError()
    {
        var reader = new SofhFrameReader();
        reader.Append(Frame(messageLength: 3, encoding: SbeEncoding, totalBytes: MinFrame));
        Assert.False(reader.TryReadFrame(out _));
        Assert.True(reader.HasProtocolError);
    }

    [Fact]
    public void MessageLengthAboveMaximum_FlagsProtocolError()
    {
        var reader = new SofhFrameReader();
        reader.Append(Frame(messageLength: MaxFrame + 1, encoding: SbeEncoding, totalBytes: MinFrame));
        Assert.False(reader.TryReadFrame(out _));
        Assert.True(reader.HasProtocolError);
        Assert.Contains("exceeds maximum", reader.ProtocolErrorMessage);
    }

    [Fact]
    public void TruncatedFrame_WaitsWithoutError()
    {
        var reader = new SofhFrameReader();
        // Claims 64 bytes but only 12 delivered — must wait, not error.
        reader.Append(Frame(messageLength: 64, encoding: SbeEncoding, totalBytes: MinFrame));
        Assert.False(reader.TryReadFrame(out _));
        Assert.False(reader.HasProtocolError);
    }

    [Fact]
    public void FuzzedHeaders_AllSizes_NeverThrow()
    {
        var rng = new Random(0x534);
        for (var i = 0; i < 5000; i++)
        {
            var reader = new SofhFrameReader();
            var msgLen = (ushort)rng.Next(0, 0xFFFF);
            var enc = (ushort)rng.Next(0, 0xFFFF);
            var bytes = rng.Next(0, MinFrame + 32);
            reader.Append(Frame(msgLen, enc, bytes));
            while (reader.TryReadFrame(out _)) { }
        }
    }

    [Fact]
    public void DripFedRandomBytes_OneAtATime_NeverThrow()
    {
        var rng = new Random(0xC0FFEE);
        var reader = new SofhFrameReader();
        for (var i = 0; i < 4096; i++)
        {
            reader.Append(new[] { (byte)rng.Next(0, 256) });
            while (reader.TryReadFrame(out _)) { }
            if (reader.HasProtocolError) { reader = new SofhFrameReader(); }
        }
    }
}
