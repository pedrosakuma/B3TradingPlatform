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
            {
                // #288 — every pipeline rejection must surface a stable
                // code. Most checks today only call
                // RiskDecision.Reject(reason); fall back to the check
                // Name (already a stable lower_snake_case identifier
                // per the IRiskCheck contract) so the REST surface and
                // the FE never see a null code on a pipeline reject.
                return decision.Code is null
                    ? decision with { Code = check.Name }
                    : decision;
            }
        }
        return RiskDecision.Approve;
    }

    public IReadOnlyList<string> CheckOrder => _checks.Select(c => c.Name).ToArray();
}
