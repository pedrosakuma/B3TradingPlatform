namespace B3.Trading.Application.Routing;

/// <summary>
/// #473 (SDK 0.15.0). Routing instruction stamped on
/// <c>NewOrderRequest.RoutingInstruction</c> /
/// <c>ReplaceOrderRequest.RoutingInstruction</c>.
///
/// <para>
/// Domain mirror of <c>B3.EntryPoint.Client.Models.RoutingInstruction</c>;
/// kept in the Application layer (parallel to
/// <see cref="Risk.SelfTradePreventionMode"/>) so the
/// <c>B3.Trading.Domain</c> project stays SDK-free. The gateway
/// translates this enum to the SDK type at the boundary.
/// </para>
///
/// <para>
/// <b>Fairness gates.</b> Each value carries different matching
/// semantics; the platform gates them with an opt-in per-scope
/// whitelist (<see cref="Risk.RiskLimits.AllowedRoutingInstructions"/>).
/// </para>
/// <list type="bullet">
///   <item><b>RetailLiquidityTaker</b> — signals retail order taking
///   liquidity (relevant to retail-flow programs).</item>
///   <item><b>WaivedPriority</b> — order waives time/price priority
///   (used by specific work-up / cross strategies). MUST be audited
///   per scope.</item>
///   <item><b>BrokerOnly</b> — order may only match internal flow of
///   the same broker. Conflict-of-interest sensitive: every stamp is
///   metric-tagged for audit.</item>
///   <item><b>BrokerOnlyRemoval</b> — removes a prior BrokerOnly
///   restriction.</item>
/// </list>
/// </summary>
public enum RoutingInstruction
{
    RetailLiquidityTaker = 1,
    WaivedPriority = 2,
    BrokerOnly = 3,
    BrokerOnlyRemoval = 4,
}
