namespace B3.Trading.Domain;

/// <summary>
/// Identity of an end-client (trader) on the platform. The platform allocates
/// per-end-client ClOrdID prefixes so that wire-level identifiers can be routed
/// back to the owning end-client on ER (mirror of the matching side's
/// OrderOwnershipMap, but participant-side).
/// </summary>
public sealed record EndClientId(string Value)
{
    public override string ToString() => Value;
}
