using B3.Trading.Domain;

namespace B3.Trading.Application.Investor;

/// <summary>
/// #472 (SDK 0.15.0). Resolves the opaque
/// <see cref="InvestorIdentity"/> stamped on every outbound
/// <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c> when known.
///
/// <para>
/// <b>Why a seam.</b> The source of truth for the (Prefix, Document)
/// mapping — broker-issued registry, CBLC association table, KYC
/// workflow, per-end-client config — is an operator decision that
/// varies between participants. The seam lets the production
/// composition root plug in a real impl without touching the gateway,
/// the order pipeline, or the WAL.
/// </para>
///
/// <para>
/// <b>Null is the safe default.</b> An order with a null InvestorId
/// leaves the wire field omitted, which is the pre-#472 behavior
/// (the venue accepts orders without the field; the broker handles
/// any out-of-band regulatory attribution). Operators opt in by
/// wiring a non-null resolver.
/// </para>
///
/// <para>
/// <b>No PII on the path.</b> Implementations MUST NOT accept or
/// return CPF/CNPJ in any field — only the opaque (Prefix, Document)
/// handle issued by the broker. See <see cref="InvestorIdentity"/>
/// for the LGPD rationale.
/// </para>
///
/// <para>
/// <b>Per-order scope + thread safety.</b> The resolver receives the
/// full <see cref="Order"/> so it can branch on owner, firm,
/// sub-account, symbol, or any combination. Implementations MUST be
/// thread-safe and side-effect-free — the gateway calls into them on
/// the hot submit/replace path.
/// </para>
/// </summary>
public interface IInvestorIdResolver
{
    /// <summary>
    /// Returns the opaque investor handle for <paramref name="order"/>,
    /// or <c>null</c> when no mapping is known (the wire field will
    /// stay omitted).
    /// </summary>
    InvestorIdentity? TryResolve(Order order);
}
