namespace B3.Trading.Application.Risk;

/// <summary>
/// Pluggable margin / collateral check. Real providers would query an
/// external risk-management system; v1 ships <see cref="NoOpMarginProvider"/>
/// so the rest of the pipeline can compose without forcing operators to
/// stand up the real integration on day one.
/// </summary>
public interface IMarginProvider
{
    Task<RiskDecision> CheckAsync(RiskContext ctx, CancellationToken ct);
}

public sealed class NoOpMarginProvider : IMarginProvider
{
    public Task<RiskDecision> CheckAsync(RiskContext ctx, CancellationToken ct) =>
        Task.FromResult(RiskDecision.Approve);
}
