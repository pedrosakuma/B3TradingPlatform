using B3.Trading.Domain;

namespace B3.Trading.Application.SubAccount;

/// <summary>
/// #458 (SDK 0.14.4+). Resolves the numeric CBLC <c>Account</c> stamped
/// on every outbound <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c>
/// when known. Unlike <see cref="ISubAccountWireIdMapper"/>, this value
/// is <b>not</b> internally derivable — CBLC account numbers are issued
/// by the clearing house and bound to a real investor identity. The
/// platform therefore models the field as <c>Nullable&lt;ulong&gt;</c>
/// end-to-end and ships a no-op default that returns <c>null</c> for
/// every order until an operator wires a real resolver (lookup table,
/// admin-managed registry, broker handshake, etc.).
///
/// <para>
/// <b>Why a seam.</b> The source of truth for the mapping (per
/// end-client? per (firmId, subAccountId)? per order via explicit
/// trader override?) is an operational decision that varies between
/// participants. The seam lets the production composition root swap
/// the no-op for a real implementation without touching the gateway,
/// the order pipeline, or the WAL.
/// </para>
///
/// <para>
/// <b>Null is the safe default.</b> An order with a null CBLC Account
/// leaves the wire field omitted; the venue then routes post-trade
/// allocation via the broker's out-of-band matching (the legacy
/// behavior pre-#458). Only opt-in operators that have configured a
/// real resolver start stamping the field.
/// </para>
///
/// <para>
/// <b>Per-order scope.</b> The resolver receives the full
/// <see cref="Order"/> so it can branch on owner, firm, sub-account,
/// symbol, or any combination thereof. Implementations MUST be
/// thread-safe and side-effect-free — the gateway calls into them on
/// the hot submit/replace path.
/// </para>
/// </summary>
public interface IVenueAccountResolver
{
    /// <summary>
    /// Returns the CBLC account number for <paramref name="order"/>,
    /// or <c>null</c> when no mapping is known (the wire field will
    /// stay omitted).
    /// </summary>
    ulong? TryResolve(Order order);
}
