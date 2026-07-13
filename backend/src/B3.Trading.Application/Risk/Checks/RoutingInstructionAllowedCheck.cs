using B3.Trading.Application.Observability;
using B3.Trading.Application.Routing;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// #473. Pre-trade gate that enforces the per-scope whitelist of
/// <see cref="RoutingInstruction"/> values
/// (<see cref="RiskLimits.AllowedRoutingInstructions"/>) against
/// whatever the submit/modify caller resolved via
/// <see cref="IRoutingInstructionResolver"/>.
///
/// <para>
/// <b>Default-deny.</b> When the resolver returned a non-null value
/// and the resolved scope has no whitelist configured (or an empty
/// one), the check rejects. This is the inverse of
/// <see cref="OrderTypeAllowedCheck"/> (which defaults to allow when
/// unconfigured) because routing instructions carry fairness /
/// conflict-of-interest implications — an operator that wires a
/// resolver MUST also explicitly opt in to the values they want
/// permitted per scope.
/// </para>
///
/// <para>
/// <b>Audit.</b> Every approved stamp increments
/// <see cref="MetricsRegistry.RoutingInstructionStamped"/> tagged
/// with the value and firm id; <c>BrokerOnly</c> in particular is
/// surfaced for downstream alerting (conflict-of-interest sensitive).
/// </para>
///
/// <para>
/// Pipeline order=16 — runs right after <see cref="OrderTypeAllowedCheck"/>
/// (15) so misconfigured routing intents short-circuit before any
/// per-instrument / margin / position work runs.
/// </para>
/// </summary>
public sealed class RoutingInstructionAllowedCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;

    public RoutingInstructionAllowedCheck(IOptionsMonitor<RiskOptions> options) => _options = options;

    public int Order => 16;
    public string Name => "routing_instruction_blocked";

    public RiskDecision Check(RiskContext ctx)
    {
        if (ctx.RoutingInstruction is not { } ri)
        {
            // Resolver yielded nothing → wire field stays omitted →
            // nothing to gate. Legacy / unmigrated callers always hit
            // this branch (they pass RoutingInstruction = null).
            return RiskDecision.Approve;
        }

        var opts = _options.CurrentValue;
        var allowed = RiskLimitsResolver.ResolveRef(
            opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol,
            l => l.AllowedRoutingInstructions,
            v => v.Count > 0);

        if (allowed is null)
        {
            // Default-deny: a resolver shipping a routing instruction
            // into a scope that has not explicitly opted in is a
            // configuration smell. Reject loudly rather than silently
            // dropping the instruction.
            return RiskDecision.Reject(
                Name,
                $"routing instruction '{ri}' resolved but no AllowedRoutingInstructions whitelist configured for this scope");
        }

        foreach (var name in allowed)
        {
            if (Enum.TryParse<RoutingInstruction>(name, ignoreCase: true, out var v) && v == ri)
            {
                MetricsRegistry.RoutingInstructionStamped.Add(1,
                    new KeyValuePair<string, object?>("value", ri.ToString()),
                    new KeyValuePair<string, object?>("firmId", ctx.FirmId));
                return RiskDecision.Approve;
            }
        }

        return RiskDecision.Reject(
            Name,
            $"routing instruction '{ri}' not in allowed list for this scope ({string.Join(",", allowed)})");
    }
}
