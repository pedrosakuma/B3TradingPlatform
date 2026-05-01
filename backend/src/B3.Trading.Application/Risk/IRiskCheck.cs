using B3.Trading.Domain;

namespace B3.Trading.Application.Risk;

/// <summary>
/// Inputs each <see cref="IRiskCheck"/> sees. Carries everything a check
/// could need without forcing checks to wire-grab from the order book or
/// position keeper themselves.
/// </summary>
public sealed record RiskContext(
    EndClientId Owner,
    string FirmId,
    string Symbol,
    OrderSide Side,
    OrderType Type,
    long Quantity,
    decimal? Price);

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
