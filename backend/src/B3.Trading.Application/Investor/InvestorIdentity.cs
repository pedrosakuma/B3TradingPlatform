namespace B3.Trading.Application.Investor;

/// <summary>
/// #472 (SDK 0.15.0). Opaque investor identifier carried in the
/// <c>InvestorId</c> wire field of <c>NewOrderRequest</c> /
/// <c>ReplaceOrderRequest</c>.
///
/// <para>
/// <b>Why this is not CPF/CNPJ.</b> The B3 EntryPoint contract reserves
/// only <c>(ushort Prefix, uint Document)</c> for the field — 6 bytes.
/// CPF (11 digits) does not fit in <c>uint</c> at all
/// (99999999999 &gt; 4294967295), and a raw CNPJ would also overflow.
/// Per operator guidance, this platform treats <c>InvestorId</c> as an
/// <b>opaque numeric handle</b> issued out-of-band (broker registry,
/// CBLC association, KYC workflow, etc.) and associated to the real
/// CPF/CNPJ at the broker — never on the wire and never in the WAL.
/// </para>
///
/// <para>
/// <b>LGPD posture.</b> The platform deliberately refuses to know the
/// CPF/CNPJ at all: orders only ever carry this opaque pair. That keeps
/// the venue protocol path completely free of PII (Lei 13.709/2018
/// art. 5º II) while preserving the venue's self-trade-prevention and
/// regulatory-attribution guarantees.
/// </para>
///
/// <para>
/// <b>Wire shape.</b> Mirrors the SDK's <c>B3.EntryPoint.Client.Models.InvestorId</c>
/// struct verbatim (<c>ushort</c> + <c>uint</c>); the gateway boundary
/// translates one to the other so the Application/Domain layers don't
/// take a dependency on the SDK type.
/// </para>
/// </summary>
public readonly record struct InvestorIdentity(ushort Prefix, uint Document);
