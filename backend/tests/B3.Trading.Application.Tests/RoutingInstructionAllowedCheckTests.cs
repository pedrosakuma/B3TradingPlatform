using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Application.Routing;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #473. Pins the default-deny semantics of the routing-instruction
/// whitelist gate. The gate must:
///   - approve when the resolver yielded nothing (pre-#473 callers)
///   - reject when the resolver yielded a value but no whitelist
///     exists at any scope (configuration smell — fail loud)
///   - reject when the value is not in the configured whitelist
///   - approve when the value matches the whitelist, and emit the
///     <c>trading.orders.routing_instruction_stamped</c> metric so
///     downstream alerting can pin BrokerOnly slices.
/// </summary>
public class RoutingInstructionAllowedCheckTests
{
    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) => new StaticOptionsMonitor<RiskOptions>(o);

    private static RiskContext Ctx(RoutingInstruction? ri) =>
        new(new EndClientId("alice"), "FIRM-A", "PETR4", OrderSide.Buy, OrderType.Limit, 100, 30m,
            RoutingInstruction: ri);

    [Fact]
    public void NullRouting_Approves_LegacyCallersStayGreen()
    {
        var check = new RoutingInstructionAllowedCheck(Wrap(new RiskOptions()));
        Assert.True(check.Check(Ctx(null)).Approved);
    }

    [Fact]
    public void Value_NoWhitelist_RejectsByDefault()
    {
        // Default-deny: a resolver shipping a value into an
        // unconfigured scope is a config smell. This is the inverse
        // of OrderTypeAllowedCheck (which defaults to allow).
        var check = new RoutingInstructionAllowedCheck(Wrap(new RiskOptions()));
        var d = check.Check(Ctx(RoutingInstruction.BrokerOnly));
        Assert.False(d.Approved);
        Assert.Contains("AllowedRoutingInstructions", d.Reason);
    }

    [Fact]
    public void Value_NotInWhitelist_Rejects()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedRoutingInstructions = new List<string> { "WaivedPriority" } },
        };
        var check = new RoutingInstructionAllowedCheck(Wrap(opts));
        var d = check.Check(Ctx(RoutingInstruction.BrokerOnly));
        Assert.False(d.Approved);
        Assert.Contains("not in allowed list", d.Reason);
    }

    [Fact]
    public void Value_InWhitelist_Approves()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedRoutingInstructions = new List<string> { "BrokerOnly", "WaivedPriority" } },
        };
        var check = new RoutingInstructionAllowedCheck(Wrap(opts));
        Assert.True(check.Check(Ctx(RoutingInstruction.BrokerOnly)).Approved);
        Assert.True(check.Check(Ctx(RoutingInstruction.WaivedPriority)).Approved);
    }

    [Fact]
    public void Whitelist_CaseInsensitive()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedRoutingInstructions = new List<string> { "brokeronly" } },
        };
        var check = new RoutingInstructionAllowedCheck(Wrap(opts));
        Assert.True(check.Check(Ctx(RoutingInstruction.BrokerOnly)).Approved);
    }

    [Fact]
    public void PipelineOrder_Is16_RightAfterOrderTypeAllowedCheck()
    {
        var check = new RoutingInstructionAllowedCheck(Wrap(new RiskOptions()));
        Assert.Equal(16, check.Order);
    }
}
