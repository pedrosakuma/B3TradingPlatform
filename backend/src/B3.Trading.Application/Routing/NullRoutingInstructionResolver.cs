using B3.Trading.Domain;

namespace B3.Trading.Application.Routing;

/// <summary>
/// Default <see cref="IRoutingInstructionResolver"/>: always returns
/// <c>null</c>. Pre-#473 wire behavior — orders carry no routing
/// instruction. Production operators that need any of the four
/// values (RetailLiquidityTaker / WaivedPriority / BrokerOnly /
/// BrokerOnlyRemoval) replace this with a real resolver at the
/// composition root.
/// </summary>
public sealed class NullRoutingInstructionResolver : IRoutingInstructionResolver
{
    public static readonly NullRoutingInstructionResolver Instance = new();

    public RoutingInstruction? TryResolve(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return null;
    }
}
