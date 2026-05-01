namespace B3.Trading.Application.Risk;

/// <summary>
/// Composes the registered <see cref="IRiskCheck"/>s into a deterministic
/// pipeline (sorted by <see cref="IRiskCheck.Order"/>, ties broken by
/// <see cref="IRiskCheck.Name"/>). Short-circuits on the first rejection.
/// </summary>
public sealed class RiskPipeline
{
    private readonly IRiskCheck[] _checks;

    public RiskPipeline(IEnumerable<IRiskCheck> checks)
    {
        _checks = checks
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public RiskDecision Evaluate(RiskContext ctx)
    {
        foreach (var check in _checks)
        {
            var decision = check.Check(ctx);
            if (!decision.Approved)
                return decision;
        }
        return RiskDecision.Approve;
    }

    public IReadOnlyList<string> CheckOrder => _checks.Select(c => c.Name).ToArray();
}
