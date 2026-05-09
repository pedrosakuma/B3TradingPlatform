namespace B3.Trading.EntryPointListener.Framing;

/// <summary>Constants for the Simple Open Framing Header (SOFH) + SBE layout.</summary>
internal static class SofhFraming
{
    /// <summary>Size of the SOFH prefix: 2-byte messageLength + 2-byte encodingType.</summary>
    public const int SofhHeaderSize = 4;

    /// <summary>Size of the SBE message header: 4 × uint16 (blockLength, templateId, schemaId, version).</summary>
    public const int SbeMessageHeaderSize = 8;

    /// <summary>SBE little-endian encoding type magic value embedded in the SOFH encodingType field.</summary>
    public const ushort SbeLittleEndianEncodingType = 0xEB50;

    /// <summary>Minimum valid frame size: SOFH + SBE header, zero-length payload.</summary>
    public const int MinFrameSize = SofhHeaderSize + SbeMessageHeaderSize;

    /// <summary>Maximum accepted frame size (prevents unbounded buffer growth on a malformed peer).</summary>
    public const int MaxFrameSize = 16_384;
}
