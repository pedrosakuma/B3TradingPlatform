namespace B3.Trading.Application.Audit;

/// <summary>
/// #435 Part B. Pass-through masker that returns the raw id verbatim
/// (no masking, no rotation, no entropy). Used by unit tests that need
/// to assert against deterministic numeric ids and by composition
/// roots that have explicitly opted out of #435 Part B gating (e.g. an
/// isolated single-firm self-clearing setup with no external drop-copy
/// consumers). NEVER bind this in a production composition root that
/// fans drop-copy out across a public WebSocket boundary.
/// </summary>
public sealed class NullClOrdIdMasker : IClOrdIdMasker
{
    public static readonly NullClOrdIdMasker Instance = new();

    private NullClOrdIdMasker() { }

    public string MaskClOrdId(string firmId, ulong clOrdId) =>
        clOrdId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string MaskAlgoId(string firmId, ulong algoId) =>
        algoId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
