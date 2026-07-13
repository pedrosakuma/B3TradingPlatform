using B3.Trading.Application.MarketData;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Q4.1 (#301). Per-(firm, sub-account) risk gate. Rejects when the
/// proposed order would push the sub-account-scoped open-order count,
/// net position, or notional past the cap configured in
/// <see cref="SubAccountRiskOptions"/>; also rejects any submit
/// targeting a soft-deleted sub-account.
///
/// <para>
/// <b>Ordering.</b> Runs at <see cref="Order"/> 175 — between
/// <see cref="MaxOpenOrdersCheck"/> (170) and
/// <see cref="PositionLimitCheck"/> (200). The master caps fire
/// first so a sub-account submit that breaches the master ceiling
/// is rejected with the legacy reason; sub-account-specific caps
/// then narrow further.
/// </para>
///
/// <para>
/// <b>Skip path.</b> When <see cref="RiskContext.SubAccountId"/> is
/// <c>null</c> the check is a complete no-op — master-bucket
/// submissions are evaluated solely by the legacy gates, preserving
/// pre-#301 behaviour exactly.
/// </para>
/// </summary>
public sealed class SubAccountLimitsCheck : IRiskCheck
{
    private readonly IOptionsMonitor<SubAccountRiskOptions> _options;
    private readonly WorkingOrderBook _book;
    private readonly SubAccountPositionKeeper _positions;
    private readonly SubAccountsRegistry _registry;
    private readonly IMarketValueCalculator _values;

    public SubAccountLimitsCheck(
        IOptionsMonitor<SubAccountRiskOptions> options,
        WorkingOrderBook book,
        SubAccountPositionKeeper positions,
        SubAccountsRegistry registry,
        IMarketValueCalculator? values = null)
    {
        _options = options;
        _book = book;
        _positions = positions;
        _registry = registry;
        _values = values ?? EquityMarketValueCalculator.Instance;
    }

    public int Order => 175;
    public string Name => "sub_account_limits";

    /// <summary>
    /// Q4.1 (#301). Distinct reason emitted when the targeted
    /// sub-account is registered-then-deactivated. Kept separate
    /// from <c>sub_account_limit_exceeded</c> (actual cap breach)
    /// so observability / client UX can differentiate "you're hard
    /// stopped" from "your knob is too tight". The REST surface
    /// mirrors the same string in its structured error
    /// (<see cref="B3.Trading.Api"/>).
    /// </summary>
    public const string DeactivatedReason = "sub_account_deactivated";
    public const string LimitExceededPrefix = "sub_account_limit_exceeded";

    public RiskDecision Check(RiskContext ctx)
    {
        if (ctx.SubAccountId is not { } sub) return RiskDecision.Approve;

        if (_registry.TryGet(ctx.FirmId, sub.Value, out var entry) && !entry.Active)
            return RiskDecision.Reject(
                $"{DeactivatedReason}: sub-account {ctx.FirmId}:{sub.Value} is deactivated");

        var limits = _options.CurrentValue.Resolve(ctx.FirmId, sub.Value);
        if (limits is null) return RiskDecision.Approve;

        if (limits.MaxOpenOrders is { } maxOpen)
        {
            // The current order is already in the book by the time
            // risk runs — match the MaxOpenOrdersCheck convention.
            var openIncludingSelf = _book.CountOpenForOwnerAndSubAccount(ctx.FirmId, ctx.Owner, sub);
            if (openIncludingSelf > maxOpen)
                return RiskDecision.Reject(
                    $"{LimitExceededPrefix}: open orders {openIncludingSelf - 1} would exceed sub-account cap {maxOpen} for {ctx.FirmId}:{sub.Value}");
        }

        if (limits.PositionLimit is { } posCap)
        {
            var pos = _positions.GetOrCreate(ctx.FirmId, ctx.Owner, sub, ctx.Symbol);
            long projectedNet;
            lock (pos)
            {
                projectedNet = ctx.Side == OrderSide.Buy
                    ? pos.NetQuantity + ctx.Quantity
                    : pos.NetQuantity - ctx.Quantity;
            }
            if (Math.Abs(projectedNet) > posCap)
                return RiskDecision.Reject(
                    $"{LimitExceededPrefix}: projected position {projectedNet} would exceed sub-account cap ±{posCap} for {ctx.FirmId}:{sub.Value} on {ctx.Symbol}");
        }

        if (limits.MaxNotional is { } notCap && ctx.Type == OrderType.Limit && ctx.Price is { } price)
        {
            // OPT-B (#484): option qty is in contracts; apply
            // contractMultiplier so MaxNotional caps options at the
            // right BRL-equivalent (silent 100x bypass without this).
            var notional = _values.GetNotional(ctx.Symbol, price, ctx.Quantity);
            if (notional > notCap)
                return RiskDecision.Reject(
                    $"{LimitExceededPrefix}: notional {notional} would exceed sub-account cap {notCap} for {ctx.FirmId}:{sub.Value}");
        }

        return RiskDecision.Approve;
    }
}
