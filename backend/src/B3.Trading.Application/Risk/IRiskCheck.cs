using B3.Trading.Domain;

namespace B3.Trading.Application.Risk;

/// <summary>
/// Inputs each <see cref="IRiskCheck"/> sees. Carries everything a check
/// could need without forcing checks to wire-grab from the order book or
/// position keeper themselves.
///
/// <para>
/// <b>Modify (cancel-replace) projection (slice 3 of #122):</b>
/// when <see cref="ReplaceOriginalClOrdId"/> is set the context
/// represents the proposed replacement of an existing working order
/// — checks that look at "open" snapshot state must therefore treat
/// the original as if it were already gone and the replacement as if
/// it were already there. <see cref="EffectiveLeavesQuantity"/> is
/// the leaves the venue will assign to the replacement
/// (<c>newQty - origCumQty</c>), used so projections stay accurate
/// when the original has partially filled.
/// </para>
/// </summary>
public sealed record RiskContext(
    EndClientId Owner,
    string FirmId,
    string Symbol,
    OrderSide Side,
    OrderType Type,
    long Quantity,
    decimal? Price,
    ulong? ReplaceOriginalClOrdId = null,
    long? EffectiveLeavesQuantity = null,
    // Q1.2 (#254). TIF / StopPrice / GoodTillDate are forwarded to the
    // pipeline so the new gates (stop-trigger, IOC/FOK + MarketWithLeftover,
    // GoodForAuction phase, GTD bounds) can run without re-reading the
    // request. Default values keep the legacy plain-Limit/Day call sites
    // ergonomic — every existing test still constructs `new RiskContext(...)`
    // with the original positional arguments.
    TimeInForce TimeInForce = TimeInForce.Day,
    decimal? StopPrice = null,
    DateTimeOffset? GoodTillDate = null,
    /// <summary>
    /// Q4.1 (#301). Optional sub-account bucket the order is booked
    /// against. Forwarded to <c>SubAccountLimitsCheck</c> so the
    /// per-(firm, subAccount) caps are applied alongside the master
    /// caps. <c>null</c> means master-only (legacy semantics).
    /// </summary>
    SubAccountId? SubAccountId = null,
    /// <summary>
    /// #473. Routing instruction the gateway intends to stamp on the
    /// outbound order (resolved via
    /// <see cref="Routing.IRoutingInstructionResolver"/> by the
    /// submit/modify caller before the pipeline runs). When non-null,
    /// <see cref="Checks.RoutingInstructionAllowedCheck"/> enforces
    /// the per-scope whitelist
    /// (<see cref="RiskLimits.AllowedRoutingInstructions"/>) and
    /// rejects pre-trade if the value is not permitted. Default null
    /// = the resolver yielded nothing → wire field will stay omitted
    /// → check is a no-op (legacy / unmigrated callers stay green).
    /// </summary>
    Routing.RoutingInstruction? RoutingInstruction = null,
    /// <summary>
    /// #435. When non-null, the request is a child order produced by
    /// the algo engine for the named parent. Throttle checks
    /// (rolling-notional, order-rate) apply a third per-algo bucket
    /// alongside the existing per-end-client / per-firm buckets, so a
    /// runaway algo can be capped without consuming the firm's entire
    /// quota. <c>null</c> = manual / non-algo origin.
    /// </summary>
    ulong? ParentAlgoId = null,
    /// <summary>
    /// #435. Algo type tag (lowercase enum name: iceberg/twap/pegged/...)
    /// used to resolve per-algo-type limits in <c>RiskOptions</c>.
    /// Only meaningful when <see cref="ParentAlgoId"/> is set.
    /// </summary>
    string? AlgoType = null);

public sealed record RiskDecision(bool Approved, string? Reason, string? Code = null)
{
    public static readonly RiskDecision Approve = new(true, null, null);
    public static RiskDecision Reject(string reason) => new(false, reason, null);

    /// <summary>
    /// #288 — stable machine-readable code. Preferred over the legacy
    /// <c>Reject(reason)</c> overload so the REST surface and the FE
    /// can branch on a fixed identifier instead of parsing the
    /// human-readable reason. <see cref="RiskPipeline.Evaluate"/> falls
    /// back to the rejecting check's <see cref="IRiskCheck.Name"/> when
    /// a check rejects without supplying its own code, so every
    /// pipeline rejection surfaces a non-null code without each check
    /// having to opt in.
    /// </summary>
    public static RiskDecision Reject(string code, string reason) => new(false, reason, code);
}

/// <summary>
/// One pre-trade rule. Implementations must be cheap and side-effect-free
/// — the pipeline runs them on the hot path of every order submission.
/// </summary>
public interface IRiskCheck
{
    /// <summary>
    /// Lower runs first. Kill-switch is 0 (fastest reject path); local
    /// limits 100; position limits 200; price collar 300.
    /// </summary>
    int Order { get; }

    string Name { get; }

    RiskDecision Check(RiskContext ctx);
}
