namespace B3.Trading.Application.Risk.Accounting;

/// <summary>
/// Records that an order has been accepted by the synchronous risk
/// pipeline <em>and</em> the async margin reservation, so the slice-7
/// throttle ledgers can advance their state. Called by the order
/// submit endpoint right before the gateway dispatch.
/// </summary>
///
/// <remarks>
/// Implementations are registered as singletons and fan out from the
/// composite <see cref="CompositeRiskAccountant"/>. Recording must be
/// quick and side-effect-free beyond ledger updates — the call is on
/// the submit hot path.
/// </remarks>
public interface IRiskAccountant
{
    void RecordAccepted(RiskContext ctx);
}

/// <summary>
/// Fans <see cref="IRiskAccountant.RecordAccepted"/> out to every
/// registered accountant. A single composite is what the endpoint
/// depends on, so adding a new accountant is a DI-only change.
/// </summary>
public sealed class CompositeRiskAccountant : IRiskAccountant
{
    private readonly IRiskAccountant[] _accountants;

    public CompositeRiskAccountant(IEnumerable<IRiskAccountant> accountants)
    {
        // Materialise once so we don't re-enumerate the DI container
        // on every submit.
        _accountants = accountants
            .Where(a => a is not CompositeRiskAccountant)
            .ToArray();
    }

    public void RecordAccepted(RiskContext ctx)
    {
        foreach (var a in _accountants) a.RecordAccepted(ctx);
    }
}
