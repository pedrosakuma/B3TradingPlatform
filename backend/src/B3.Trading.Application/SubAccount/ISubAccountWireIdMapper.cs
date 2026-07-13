using B3.Trading.Domain;

namespace B3.Trading.Application.SubAccount;

/// <summary>
/// #471 (SDK 0.15.0). Maps a domain <see cref="SubAccountId"/> (string,
/// case-sensitive, namespaced per-firm) to the numeric
/// <c>TradingSubAccount</c> (<c>uint?</c>) carried on every outbound
/// <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c> when present.
///
/// <para>
/// <b>Why a seam.</b> The B3 wire field is numeric per-session and has
/// no upstream registration handshake on our side today (#471 RFC). The
/// platform therefore generates the wire id from the domain id via an
/// internal stable function rather than via a venue-issued mapping. The
/// seam lets an operator swap the default deterministic hash for an
/// explicit lookup table the day they negotiate a registered mapping
/// with the broker, without touching the gateway.
/// </para>
///
/// <para>
/// <b>Null in, null out.</b> Per the RFC: <c>TradingSubAccount</c> is
/// nullable on the wire (not every order carries a sub-account); a
/// <c>null</c> domain id MUST produce a <c>null</c> wire id so the
/// SDK omits the field entirely. Non-null inputs MUST produce non-null,
/// non-zero outputs (zero is reserved to defend against accidental
/// "looks unset" interop bugs in any future consumer).
/// </para>
/// </summary>
public interface ISubAccountWireIdMapper
{
    /// <summary>
    /// Maps the (firm, sub-account) pair to the wire id. The firm is
    /// part of the input because <see cref="SubAccountId"/> is
    /// namespaced per-firm (see <see cref="SubAccountsRegistry"/>)
    /// and the wire id must be deterministic <i>within</i> a firm's
    /// session — different firms may produce different ids for the
    /// same domain string.
    /// </summary>
    /// <returns>
    /// <c>null</c> iff <paramref name="subAccountId"/> is <c>null</c>;
    /// otherwise a non-zero <see cref="uint"/>.
    /// </returns>
    uint? TryMap(string firmId, SubAccountId? subAccountId);
}
