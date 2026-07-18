namespace B3.Trading.Application;

/// <summary>The business operation represented by an outbound gateway attempt.</summary>
public enum ExchangeGatewayOperation
{
    NewOrder,
    Cancel,
    Replace,
}

/// <summary>
/// Last stage for which the gateway has typed evidence. No stage represents
/// venue acceptance.
/// </summary>
public enum ExchangeGatewayAttemptStage
{
    NotStarted,
    SequenceReserved,
    SequenceReservedAndEncoded,
    FramePrepared,
    TransportWriteStarted,
    TransportWriteCompleted,
    SdkSessionStatePersisted,
}

/// <summary>Typed failure interpretation available to the outbound coordinator.</summary>
public enum ExchangeGatewayFailureDisposition
{
    /// <summary>The invocation cannot perform a transport write now or later.</summary>
    OutboundProvenUnsent,

    /// <summary>A partial or completed transport write cannot be excluded.</summary>
    Ambiguous,
}

/// <summary>
/// Immutable identity of the actual encoded frame prepared for one firm-scoped
/// FIXP session.
/// </summary>
/// <remarks>
/// <see cref="EncodedFrameSha256"/> is the SDK-provided SHA-256 over the complete
/// SOFH-framed encoded bytes, canonicalized to lowercase hexadecimal for direct
/// persistence in the outbound ledger. The frame payload itself is never
/// retained or exposed by this contract.
/// </remarks>
public sealed record ExchangeGatewayFrameIdentity
{
    public ExchangeGatewayFrameIdentity(
        string firmId,
        ulong sessionId,
        uint sessionVerId,
        ulong outboundSeqNum,
        ExchangeGatewayOperation operation,
        ulong clOrdId,
        int encodedFrameLength,
        string encodedFrameSha256)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("Firm id is required.", nameof(firmId));
        if (sessionId == 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId), "Session id must be non-zero.");
        if (sessionVerId == 0)
            throw new ArgumentOutOfRangeException(nameof(sessionVerId), "Session version id must be non-zero.");
        if (outboundSeqNum == 0)
            throw new ArgumentOutOfRangeException(nameof(outboundSeqNum), "Outbound sequence number must be non-zero.");
        if (clOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(clOrdId), "ClOrdID must be non-zero.");
        if (encodedFrameLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(encodedFrameLength), "Encoded frame length must be positive.");
        if (!IsSha256Hex(encodedFrameSha256))
            throw new ArgumentException("Encoded frame hash must be a 64-character SHA-256 hexadecimal value.", nameof(encodedFrameSha256));

        FirmId = firmId;
        SessionId = sessionId;
        SessionVerId = sessionVerId;
        OutboundSeqNum = outboundSeqNum;
        Operation = operation;
        ClOrdId = clOrdId;
        EncodedFrameLength = encodedFrameLength;
        EncodedFrameSha256 = encodedFrameSha256.ToLowerInvariant();
    }

    public string FirmId { get; }
    public ulong SessionId { get; }
    public uint SessionVerId { get; }
    public ulong OutboundSeqNum { get; }
    public ExchangeGatewayOperation Operation { get; }
    public ulong ClOrdId { get; }
    public int EncodedFrameLength { get; }
    public string EncodedFrameSha256 { get; }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
            return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Durable write gate invoked after sequence reservation and encoding, but
/// before the first possible transport write.
/// </summary>
/// <remarks>
/// The callback runs while the SDK serializes outbound sends and must not
/// re-enter the same gateway. A dead epoch with attempt intent but no committed
/// callback can be classified as unsent only after SDK sequence-state
/// reconciliation confirms the contracted no-write boundary.
/// </remarks>
public delegate ValueTask ExchangeGatewayFramePreparedCallback(
    ExchangeGatewayFrameIdentity frame,
    CancellationToken cancellationToken);

/// <summary>
/// Successful local completion evidence. This is transport completion only,
/// never venue acceptance.
/// </summary>
public sealed record ExchangeGatewayReceipt
{
    public const int CurrentVersion = 1;

    public ExchangeGatewayReceipt(
        ExchangeGatewayFrameIdentity frame,
        ExchangeGatewayAttemptStage lastStage)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (lastStage < ExchangeGatewayAttemptStage.TransportWriteCompleted)
            throw new ArgumentOutOfRangeException(
                nameof(lastStage),
                lastStage,
                "A successful receipt must prove local transport write completion.");

        Frame = frame;
        LastStage = lastStage;
    }

    public ExchangeGatewayFrameIdentity Frame { get; }
    public ExchangeGatewayAttemptStage LastStage { get; }
    public int Version => CurrentVersion;
}

/// <summary>Failure with typed outbound evidence from the gateway boundary.</summary>
/// <remarks>
/// Generic failures are never promoted to
/// <see cref="ExchangeGatewayFailureDisposition.OutboundProvenUnsent"/>.
/// At or after frame preparation the outcome remains ambiguous unless the SDK's
/// typed contract explicitly proves that no transport write is possible.
/// </remarks>
public sealed class ExchangeGatewayAttemptException : Exception
{
    public ExchangeGatewayAttemptException(
        string message,
        ExchangeGatewayFailureDisposition disposition,
        ExchangeGatewayAttemptStage lastStage,
        ExchangeGatewayFrameIdentity? frame,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Disposition = disposition;
        LastStage = lastStage;
        Frame = frame;
    }

    public ExchangeGatewayFailureDisposition Disposition { get; }
    public ExchangeGatewayAttemptStage LastStage { get; }
    public ExchangeGatewayFrameIdentity? Frame { get; }
    public bool NoTransportWritePossible =>
        Disposition == ExchangeGatewayFailureDisposition.OutboundProvenUnsent;

    public static ExchangeGatewayAttemptException ReceiptNotSupported() =>
        new(
            "This exchange gateway does not support durable outbound receipts; no transport write was attempted.",
            ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
            ExchangeGatewayAttemptStage.NotStarted,
            frame: null);
}
