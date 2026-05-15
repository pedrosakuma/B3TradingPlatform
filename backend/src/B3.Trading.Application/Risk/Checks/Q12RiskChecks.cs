using System.Globalization;
using B3.Trading.Application.Observability;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Q1.2 (#254). Stop-trigger sanity for <see cref="OrderType.StopLoss"/>
/// and <see cref="OrderType.StopLimit"/>.
///
/// <para><b>Rules (layered on top of the domain invariant that
/// <c>StopPrice</c> is required + positive for Stop* orders):</b></para>
/// <list type="bullet">
///   <item><description>Buy stop ⇒ <c>StopPrice &gt;= ref</c> (you "stop" once price rises into the trigger).</description></item>
///   <item><description>Sell stop ⇒ <c>StopPrice &lt;= ref</c>.</description></item>
///   <item><description><c>StopLimit</c> additionally requires <c>Price</c> to be present and to honour <c>Buy: Price &gt;= StopPrice</c> / <c>Sell: Price &lt;= StopPrice</c> — a sell-stop-limit with a limit ABOVE the trigger would never fill once triggered.</description></item>
/// </list>
///
/// <para><b>Lenient skip on missing reference:</b> if the platform has
/// no reference price for the symbol the relation check is skipped
/// (only the <c>StopPrice &gt; 0</c> invariant — already enforced by
/// the domain — applies). The
/// <see cref="MetricsRegistry.StopCheckSkippedNoRef"/> counter is bumped
/// with a <c>symbol</c> tag so ops can spot coverage gaps. Same
/// fail-open posture as <see cref="PriceCollarCheck"/>: a configured
/// guard with no anchor cannot be enforced, and a hard reject would
/// silently halt Stop* trading on every symbol the MD feed hasn't
/// covered yet.</para>
///
/// <para>Pipeline order=305 — runs right after <see cref="PriceCollarCheck"/>
/// (300) so the cheaper price-band gate short-circuits first.</para>
/// </summary>
public sealed class StopTriggerCheck : IRiskCheck
{
    private readonly IReferencePrice _refPrice;

    public StopTriggerCheck(IReferencePrice refPrice) => _refPrice = refPrice;

    public int Order => 305;
    public string Name => "stop_trigger";

    public RiskDecision Check(RiskContext ctx)
    {
        if (ctx.Type is not (OrderType.StopLoss or OrderType.StopLimit))
            return RiskDecision.Approve;

        // Domain guarantees StopPrice is present and > 0 for Stop*; we
        // re-verify defensively because the risk pipeline runs on the
        // RiskContext, not on a constructed Order.
        if (!ctx.StopPrice.HasValue || ctx.StopPrice.Value <= 0m)
            return RiskDecision.Reject(
                $"stop_trigger_invalid stopPrice={Format(ctx.StopPrice)} side={ctx.Side.ToString().ToLowerInvariant()}");

        var stop = ctx.StopPrice.Value;

        if (ctx.Type == OrderType.StopLimit)
        {
            if (!ctx.Price.HasValue)
                return RiskDecision.Reject(
                    $"stop_limit_price_invalid stopPrice={Format(stop)} side={ctx.Side.ToString().ToLowerInvariant()} price=null");
            var limit = ctx.Price.Value;
            var limitOk = ctx.Side == OrderSide.Buy ? limit >= stop : limit <= stop;
            if (!limitOk)
                return RiskDecision.Reject(
                    $"stop_limit_price_invalid stopPrice={Format(stop)} price={Format(limit)} side={ctx.Side.ToString().ToLowerInvariant()}");
        }

        var lookup = _refPrice.Lookup(ctx.Symbol);
        if (!lookup.Found || lookup.Price <= 0m)
        {
            // Lenient skip: emit observability and approve. See class
            // doc — fail-open consistent with PriceCollarCheck.
            MetricsRegistry.StopCheckSkippedNoRef.Add(1,
                new KeyValuePair<string, object?>("symbol", ctx.Symbol));
            return RiskDecision.Approve;
        }

        var refPx = lookup.Price;
        var stopOk = ctx.Side == OrderSide.Buy ? stop >= refPx : stop <= refPx;
        if (!stopOk)
            return RiskDecision.Reject(
                $"stop_trigger_invalid stopPrice={Format(stop)} ref={Format(refPx)} side={ctx.Side.ToString().ToLowerInvariant()}");
        return RiskDecision.Approve;
    }

    private static string Format(decimal? v) =>
        v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "null";
    private static string Format(decimal v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Q1.2 (#254). <see cref="TimeInForce.IOC"/> and
/// <see cref="TimeInForce.FOK"/> are immediate-match TIFs;
/// <see cref="OrderType.MarketWithLeftover"/> explicitly intends to
/// rest the un-filled remainder on the book. The combination is
/// nonsensical (the venue would either reject it or silently downgrade
/// one of the two), so we reject up-front rather than route an
/// ambiguous order to the gateway.
///
/// <para>Pipeline order=20 — early and cheap (no allocations / no IO).</para>
/// </summary>
public sealed class IocFokMarketWithLeftoverCheck : IRiskCheck
{
    public int Order => 20;
    public string Name => "tif_incompatible_with_market_with_leftover";

    public RiskDecision Check(RiskContext ctx)
    {
        if (ctx.Type != OrderType.MarketWithLeftover) return RiskDecision.Approve;
        if (ctx.TimeInForce is TimeInForce.IOC or TimeInForce.FOK)
            return RiskDecision.Reject(
                $"tif_incompatible_with_market_with_leftover tif={ctx.TimeInForce}");
        return RiskDecision.Approve;
    }
}

/// <summary>
/// Q1.2 (#254). <see cref="TimeInForce.GoodForAuction"/> is only
/// meaningful inside a call auction. Reject when the current
/// <see cref="TradingPhase"/> for the symbol is not
/// <see cref="TradingPhase.OpeningCall"/> or
/// <see cref="TradingPhase.FinalClosingCall"/>.
///
/// <para><b>Default provider stub:</b> in the absence of #257
/// (auction-MD ingest) the registered <see cref="IPhaseProvider"/> is
/// <see cref="NoPhaseProvider"/> which always reports
/// <see cref="TradingPhase.Open"/>. Under the stub every GFA submission
/// is rejected — the intended fail-closed posture, since accepting a
/// GFA into continuous matching has no meaning. Tests that need to
/// exercise the accept path inject a fake provider preset to
/// <see cref="TradingPhase.OpeningCall"/> / <see cref="TradingPhase.FinalClosingCall"/>.</para>
///
/// <para>Pipeline order=14 — right after <see cref="SessionPhaseCheck"/>
/// (12) and before any IO-bearing gate.</para>
/// </summary>
public sealed class GoodForAuctionPhaseCheck : IRiskCheck
{
    private readonly IPhaseProvider _phases;
    public GoodForAuctionPhaseCheck(IPhaseProvider phases) => _phases = phases;

    public int Order => 14;
    public string Name => "gfa_outside_auction_phase";

    public RiskDecision Check(RiskContext ctx)
    {
        if (ctx.TimeInForce != TimeInForce.GoodForAuction) return RiskDecision.Approve;
        var phase = _phases.GetPhase(ctx.Symbol);
        if (phase is TradingPhase.OpeningCall or TradingPhase.FinalClosingCall)
            return RiskDecision.Approve;
        return RiskDecision.Reject($"gfa_outside_auction_phase phase={phase}");
    }
}

/// <summary>
/// Q1.2 (#254). <see cref="TimeInForce.GTD"/> bounds:
/// <c>GoodTillDate</c> must be present (domain-enforced), strictly in
/// the future, and within
/// <see cref="RiskOptions.MaxGtdHorizon"/> (default 30 days).
///
/// <para>Time source is <see cref="TimeProvider"/> from DI so tests
/// can pin "now" with a <c>FakeTimeProvider</c>; production resolves
/// to <see cref="TimeProvider.System"/> via the existing host
/// registration.</para>
///
/// <para>Pipeline order=22 — early and cheap.</para>
/// </summary>
public sealed class GtdBoundsCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly TimeProvider _clock;

    public GtdBoundsCheck(IOptionsMonitor<RiskOptions> options, TimeProvider? clock = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public int Order => 22;
    public string Name => "gtd_bounds";

    public RiskDecision Check(RiskContext ctx)
    {
        if (ctx.TimeInForce != TimeInForce.GTD) return RiskDecision.Approve;
        if (!ctx.GoodTillDate.HasValue)
            return RiskDecision.Reject("gtd_invalid expiry=null");

        var now = _clock.GetUtcNow();
        var maxHorizon = _options.CurrentValue.MaxGtdHorizon;
        if (maxHorizon <= TimeSpan.Zero) maxHorizon = TimeSpan.FromDays(30);

        var expiry = ctx.GoodTillDate.Value;
        var horizonDays = (int)Math.Round(maxHorizon.TotalDays);
        if (expiry <= now)
            return RiskDecision.Reject(
                $"gtd_invalid expiry={Iso(expiry)} now={Iso(now)} maxHorizonDays={horizonDays}");
        if (expiry - now > maxHorizon)
            return RiskDecision.Reject(
                $"gtd_invalid expiry={Iso(expiry)} now={Iso(now)} maxHorizonDays={horizonDays}");
        return RiskDecision.Approve;
    }

    private static string Iso(DateTimeOffset t) =>
        t.ToString("O", CultureInfo.InvariantCulture);
}
