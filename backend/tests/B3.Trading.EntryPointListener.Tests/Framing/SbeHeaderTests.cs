using System.Buffers.Binary;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Framing;

namespace B3.Trading.EntryPointListener.Tests.Framing;

public class SbeHeaderTests
{
    /// <summary>
    /// Verifies the SBE MessageHeader wire layout: 4 consecutive uint16 LE fields
    /// at offsets 0 (blockLength), 2 (templateId), 4 (schemaId), 6 (version).
    /// </summary>
    [Fact]
    public void MessageHeader_WireLayout_IsFourConsecutiveUInt16LE()
    {
        var hdr = new MessageHeader
        {
            BlockLength = 0x1234,
            TemplateId = 0x5678,
            SchemaId = 0x9ABC,
            Version = 0xDEF0,
        };

        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref hdr, 1));

        Assert.Equal(8, bytes.Length);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
        Assert.Equal(0x5678, BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]));
        Assert.Equal(0x9ABC, BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]));
        Assert.Equal(0xDEF0, BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]));
    }

    [Fact]
    public void FramingHeader_WireLayout_IsMessageLengthThenEncodingType()
    {
        var hdr = new FramingHeader
        {
            MessageLength = 0x0028,
            EncodingType = SofhFraming.SbeLittleEndianEncodingType,
        };

        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref hdr, 1));

        Assert.Equal(4, bytes.Length);
        Assert.Equal(0x0028, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
        Assert.Equal(SofhFraming.SbeLittleEndianEncodingType, BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]));
    }
}
