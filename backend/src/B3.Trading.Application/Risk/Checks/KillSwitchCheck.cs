namespace B3.Trading.Application.Risk.Checks;

public sealed class KillSwitchCheck : IRiskCheck
{
    private readonly KillSwitchService _killSwitch;
    public KillSwitchCheck(KillSwitchService killSwitch) => _killSwitch = killSwitch;

    public int Order => 0;
    public string Name => "kill_switch";

    public RiskDecision Check(RiskContext ctx)
    {
        if (_killSwitch.IsEndClientKilled(ctx.Owner))
            return RiskDecision.Reject($"end-client '{ctx.Owner.Value}' kill-switch active");
        if (_killSwitch.IsFirmKilled(ctx.FirmId))
            return RiskDecision.Reject($"firm '{ctx.FirmId}' kill-switch active");
        return RiskDecision.Approve;
    }
}
