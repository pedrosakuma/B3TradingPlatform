namespace B3.Trading.EntryPointListener.Framing;

/// <summary>
/// A decoded SOFH-framed SBE message, returned by
/// <see cref="SofhFrameReader.TryReadFrame"/>.
/// The <see cref="Payload"/> span is valid until the next
/// <see cref="SofhFrameReader.Append"/> call.
/// </summary>
internal readonly ref struct SofhFrame
{
    public SofhFrame(
        ushort blockLength,
        ushort templateId,
        ushort schemaId,
        ushort version,
        ReadOnlySpan<byte> payload)
    {
        BlockLength = blockLength;
        TemplateId = templateId;
        SchemaId = schemaId;
        Version = version;
        Payload = payload;
    }

    /// <summary>SBE message block length (from the SBE message header).</summary>
    public ushort BlockLength { get; }

    /// <summary>SBE template ID — used for message dispatch.</summary>
    public ushort TemplateId { get; }

    /// <summary>SBE schema ID.</summary>
    public ushort SchemaId { get; }

    /// <summary>SBE schema version.</summary>
    public ushort Version { get; }

    /// <summary>
    /// Bytes after the 8-byte SBE message header up to the end of the frame.
    /// Length = messageLength − SofhHeaderSize(4) − SbeMessageHeaderSize(8).
    /// </summary>
    public ReadOnlySpan<byte> Payload { get; }
}
