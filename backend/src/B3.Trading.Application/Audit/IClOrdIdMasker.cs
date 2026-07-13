namespace B3.Trading.Application.Audit;

/// <summary>
/// #435 Part B. Opaque, time-rotated handle used in place of the
/// raw <c>ClOrdId</c> / <c>ParentAlgoId</c> on externally-observable
/// surfaces (today: the drop-copy stream). The contract is:
///
/// <list type="bullet">
///   <item><b>Deterministic within a (firmId, UTC day)</b> so the
///   legitimate drop-copy consumer can still group consecutive child
///   events of the same order / algo within the same trading day.</item>
///   <item><b>Unlinkable across days</b> so a counterparty consuming
///   a downstream tap of the drop-copy channel cannot reconstruct an
///   algo's footprint across multiple sessions.</item>
///   <item><b>Unlinkable across firms</b> so cross-firm correlation
///   is impossible even if a single tap receives multi-firm traffic.</item>
///   <item><b>One-way</b>: the host's internal index (<c>WorkingOrderBook</c>,
///   <c>OrderOwnershipMap</c>, <c>ClOrdIdPrefixRegistry</c>) continues
///   to use raw IDs — masking happens only at the DTO projection
///   that crosses the drop-copy WebSocket boundary.</item>
/// </list>
///
/// <para>
/// <b>Why two methods, not one.</b> <see cref="MaskClOrdId"/> and
/// <see cref="MaskAlgoId"/> hash the same <c>ulong</c> domain but
/// must produce different outputs for the same numeric id, otherwise
/// an algo whose <c>ParentAlgoId</c> happens to equal the
/// <c>ClOrdId</c> of an unrelated order would leak that coincidence
/// to the drop-copy consumer.
/// </para>
/// </summary>
public interface IClOrdIdMasker
{
    /// <summary>Mask a child / standalone order's wire ClOrdId.</summary>
    string MaskClOrdId(string firmId, ulong clOrdId);

    /// <summary>Mask an algo parent identifier.</summary>
    string MaskAlgoId(string firmId, ulong algoId);
}
