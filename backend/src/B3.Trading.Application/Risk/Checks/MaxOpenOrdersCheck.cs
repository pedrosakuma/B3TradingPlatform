using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Caps the number of non-terminal (PendingNew / Working /
/// PartiallyFilled) orders an end-client can have outstanding.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Counting semantics.</b> The current order being submitted is
/// already in <see cref="WorkingOrderBook"/> by the time the risk
/// pipeline runs (<see cref="WorkingOrderBook.TryAdd"/> is called by
/// the persistence dispatcher before
/// <c>RiskPipeline.Evaluate</c>), so the check uses strict
/// <c>&gt;</c> against the cap rather than <c>&gt;=</c>: a cap of N
/// is reached exactly when the count after include-self is N+1.
/// </para>
///
/// <para>
/// <b>State lifetime.</b> The order book is currently in-memory and
/// re-derived from ER replay on restart, so caps reset across
/// restarts. Documented in the persistence spike (checkpoint 015) as
/// an MVP limitation tied to the broader event-sourcing roadmap.
/// </para>
/// </remarks>
public sealed class MaxOpenOrdersCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly WorkingOrderBook _book;

    public MaxOpenOrdersCheck(IOptionsMonitor<RiskOptions> options, WorkingOrderBook book)
    {
        _options = options;
        _book = book;
    }

    public int Order => 170;
    public string Name => "max_open_orders";

    public RiskDecision Check(RiskContext ctx)
    {
        var cap = RiskLimitsResolver.Resolve(
            _options.CurrentValue, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.MaxOpenOrders);
        if (!cap.HasValue) return RiskDecision.Approve;

        // The current order is already in the book — see XML doc.
        var openIncludingSelf = _book.CountOpenForOwner(ctx.Owner);
        if (openIncludingSelf > cap.Value)
            return RiskDecision.Reject(
                $"open orders {openIncludingSelf - 1} would exceed cap {cap.Value} for {ctx.Owner.Value}");
        return RiskDecision.Approve;
    }
}
