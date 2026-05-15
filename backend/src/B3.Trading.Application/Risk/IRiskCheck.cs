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
    DateTimeOffset? GoodTillDate = null);

public sealed record RiskDecision(bool Approved, string? Reason)
{
    public static readonly RiskDecision Approve = new(true, null);
    public static RiskDecision Reject(string reason) => new(false, reason);
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
