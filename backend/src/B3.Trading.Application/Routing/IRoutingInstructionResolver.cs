using B3.Trading.Domain;

namespace B3.Trading.Application.Routing;

/// <summary>
/// #473 (SDK 0.15.0). Resolves the <see cref="RoutingInstruction"/>
/// stamped on every outbound <c>NewOrderRequest</c> /
/// <c>ReplaceOrderRequest</c> when known.
///
/// <para>
/// <b>Why a seam.</b> The platform deliberately refuses to invent a
/// trader's routing intent — operators decide their own policy
/// (per-end-client tag, broker-only liquidity desk, retail-flow
/// program participation, etc.) and plug it in here. Default
/// behavior is <see cref="NullRoutingInstructionResolver"/> (always
/// null → wire field omitted, pre-#473 behavior).
/// </para>
///
/// <para>
/// <b>Defense in depth.</b> Whatever this resolver returns is gated
/// by <see cref="Risk.RiskLimits.AllowedRoutingInstructions"/>
/// at pre-trade time
/// (<see cref="Risk.Checks.RoutingInstructionAllowedCheck"/>). Even
/// a misconfigured resolver cannot ship a forbidden instruction past
/// the risk pipeline.
/// </para>
///
/// <para>
/// <b>Determinism contract.</b> The same <see cref="Order"/> may be
/// resolved twice in a submit lifecycle: once at risk-pipeline time
/// (to gate against the whitelist) and once at gateway stamp time.
/// Implementations MUST therefore be deterministic per-<c>Order</c>
/// — reading mutable global state mid-submit risks an approve/stamp
/// mismatch. Thread-safe and side-effect-free are also required
/// because the gateway calls into them on the hot submit/replace
/// path.
/// </para>
/// </summary>
public interface IRoutingInstructionResolver
{
    /// <summary>
    /// Returns the routing instruction to stamp for
    /// <paramref name="order"/>, or <c>null</c> when no instruction
    /// applies (the wire field stays omitted).
    /// </summary>
    RoutingInstruction? TryResolve(Order order);
}
