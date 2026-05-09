using System.Buffers.Binary;

namespace B3.Trading.EntryPointListener.Framing;

/// <summary>
/// Helpers for writing SOFH-framed SBE messages into a destination buffer.
/// All multi-byte fields are written little-endian per the B3 FIXP V6 spec.
/// </summary>
internal static class SofhFrameWriter
{
    /// <summary>Total byte count for a frame whose SBE body is <paramref name="sbeBodyLength"/> bytes.</summary>
    public static int FrameSize(int sbeBodyLength)
        => SofhFraming.SofhHeaderSize + SofhFraming.SbeMessageHeaderSize + sbeBodyLength;

    /// <summary>
    /// Writes only the 4-byte SOFH header (messageLength + encodingType) into
    /// the start of <paramref name="destination"/>.
    /// </summary>
    public static void WriteHeader(Span<byte> destination, ushort messageLength)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, messageLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], SofhFraming.SbeLittleEndianEncodingType);
    }

    /// <summary>
    /// Writes a complete SOFH-framed SBE message into <paramref name="destination"/>.
    /// The destination must be at least <see cref="FrameSize"/>(<paramref name="body"/>.Length) bytes.
    /// </summary>
    public static void WriteFrame(
        Span<byte> destination,
        ushort blockLength,
        ushort templateId,
        ushort schemaId,
        ushort version,
        ReadOnlySpan<byte> body)
    {
        var messageLength = (ushort)(SofhFraming.SofhHeaderSize + SofhFraming.SbeMessageHeaderSize + body.Length);
        WriteHeader(destination, messageLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], blockLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], templateId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], schemaId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], version);
        body.CopyTo(destination[12..]);
    }
}
