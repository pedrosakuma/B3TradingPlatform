namespace B3.Trading.Application.Risk;

/// <summary>
/// Venue-side self-trade-prevention instruction emitted on outbound
/// <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c>. Maps 1:1 to
/// <c>B3.EntryPoint.Client.Models.SelfTradePreventionInstruction</c>
/// (added in SDK 0.15.0) — kept as an Application-layer enum so the
/// Application + Domain projects do not gain a transitive reference
/// to the wire library and so the same value can be plumbed through
/// the in-process mock seam if/when needed.
///
/// <para>
/// This is the wire-side belt-and-braces companion to the pre-trade
/// <see cref="Checks.SelfTradePreventionCheck"/>. The pre-trade check
/// continues to reject same-firm same-owner crosses synchronously
/// (gateway dispatch never even fires) when
/// <see cref="RiskLimits.AllowSelfTrade"/> is not <c>true</c>; the
/// instruction below is what the venue matching engine consults if a
/// cross still reaches the book — for example a same-firm cross
/// between two different end-clients (which the pre-trade check
/// intentionally allows), or any pair the platform missed because
/// the working-order snapshot was stale.
/// </para>
/// </summary>
public enum SelfTradePreventionMode
{
    /// <summary>
    /// Do not send any STP instruction (SDK <c>None</c>). The venue
    /// applies its own default — historically "no STP", i.e. a
    /// self-cross will execute. Use only when the operator has a
    /// specific reason to suppress venue-side STP (test accounts,
    /// market-makers running an external risk layer).
    /// </summary>
    None = 0,

    /// <summary>
    /// Cancel the incoming (aggressor) order — the side that
    /// would have crossed against its own resting order. Equivalent
    /// to the pre-trade check's "newest rejects" stance and is the
    /// sensible default for retail/buy-side flow.
    /// </summary>
    CancelAggressorOrder = 1,

    /// <summary>
    /// Cancel the resting opposite-side order — let the aggressor
    /// trade as if the resting order weren't there. Common for
    /// market-makers who want their newest quote to win over a
    /// stale own quote.
    /// </summary>
    CancelRestingOrder = 2,

    /// <summary>
    /// Cancel both orders. Used by some prop / arbitrage strategies
    /// that prefer to clear the book of any self-cross than to keep
    /// either side resting.
    /// </summary>
    CancelBothOrders = 3,
}
