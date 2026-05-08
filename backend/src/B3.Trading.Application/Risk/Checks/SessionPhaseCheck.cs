using B3.Trading.Domain;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Pre-trade gate that rejects orders incompatible with the current
/// <see cref="SessionPhase"/> for the symbol (#108).
///
/// <para><b>Rules</b> (deliberately conservative — auctions only accept
/// limit orders in B3 reality, after-hours is limit-only too, and a
/// closed venue accepts nothing):</para>
/// <list type="bullet">
///   <item><description><c>Closed</c> → reject every order. Reason: <c>phase_not_allowed:closed</c>.</description></item>
///   <item><description><c>PreOpening</c>, <c>OpeningAuction</c>, <c>ClosingAuction</c> → reject <c>Market</c>; allow <c>Limit</c>. Reason: <c>phase_not_allowed:auction</c>.</description></item>
///   <item><description><c>AfterHours</c> → reject <c>Market</c>; allow <c>Limit</c>. Reason: <c>phase_not_allowed:after_hours</c>.</description></item>
///   <item><description><c>Continuous</c> → approve.</description></item>
/// </list>
///
/// <para>Pipeline order is 12 — after the kill-switch (0) and the
/// halt check (10), before tick/lot/notional and any throttle. We
/// fail fast on the cheapest binary control once the symbol is known
/// not halted; no need to spend cycles on instrument rules for an
/// order the venue won't even consider.</para>
///
/// <para>If/when <c>TimeInForce</c> lands on the order model, this
/// check should be extended to reject IOC/FOK in auction phases too.</para>
/// </summary>
public sealed class SessionPhaseCheck : IRiskCheck
{
    private readonly SessionPhaseService _phases;
    public SessionPhaseCheck(SessionPhaseService phases) => _phases = phases;

    public int Order => 12;
    public string Name => "phase_not_allowed";

    public RiskDecision Check(RiskContext ctx)
    {
        var phase = _phases.GetPhase(ctx.Symbol);
        return phase switch
        {
            SessionPhase.Closed =>
                RiskDecision.Reject($"phase_not_allowed:closed (symbol '{ctx.Symbol}' venue closed)"),
            SessionPhase.PreOpening or SessionPhase.OpeningAuction or SessionPhase.ClosingAuction
                when ctx.Type == OrderType.Market =>
                RiskDecision.Reject($"phase_not_allowed:auction (market orders not accepted in {phase} for '{ctx.Symbol}')"),
            SessionPhase.AfterHours when ctx.Type == OrderType.Market =>
                RiskDecision.Reject($"phase_not_allowed:after_hours (market orders not accepted after-hours for '{ctx.Symbol}')"),
            _ => RiskDecision.Approve,
        };
    }
}
