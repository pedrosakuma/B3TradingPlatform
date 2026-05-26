using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// OPT-E (#487, refs #482 OPT-readiness umbrella). Pre-trade gate
/// that enforces the venue's authoritative dynamic price band
/// (<c>PriceBand_22</c>, projected by the SDK 0.6.0 <c>PriceBand</c>
/// channel into <see cref="IPriceBandSource"/>) on every LIMIT-style
/// order.
///
/// <para>
/// Relationship to <see cref="PriceCollarCheck"/>:
/// <list type="bullet">
///   <item><see cref="PriceCollarCheck"/> (Order=300) is the static
///         fat-finger envelope sourced from operator config plus
///         <see cref="IReferencePrice"/>. It runs for every symbol
///         regardless of MD coverage and fails open when no
///         reference is known.</item>
///   <item><see cref="PriceBandCheck"/> (Order=305) consults the
///         venue's published band — the same envelope the matching
///         engine itself uses. When the band is present and the
///         price violates it, the order is rejected before the wire
///         (avoids a venue-side rejection round-trip and surfaces a
///         stable <see cref="RiskRejectCodes.PriceBand"/> code so the
///         FE / drop-copy / surveillance pipelines can branch on it).
///         When the band is absent — pre-bootstrap window, kill-switch
///         off, or a symbol the venue never publishes — the check
///         fails open with a bypass counter and the static collar
///         continues to gate.</item>
/// </list>
/// </para>
///
/// <para>
/// Fail-open posture mirrors <see cref="PriceCollarCheck"/>:
/// missing source must not stop trading on a configured symbol.
/// <see cref="MetricsRegistry.PriceBandBypassedNoBand"/> is the
/// only signal ops gets that the check was inert — a steady
/// non-zero rate on a known-publishing symbol means the SDK feed
/// degraded and the static collar is now the only line of defence.
/// </para>
///
/// <para>
/// Market orders (<c>ctx.Price is null</c>) are skipped — there is no
/// price to validate. Stop orders are evaluated against their
/// <c>Price</c> (the post-trigger limit), not <c>StopPrice</c>, same
/// as <see cref="PriceCollarCheck"/>.
/// </para>
/// </summary>
public sealed class PriceBandCheck : IRiskCheck
{
    private readonly IPriceBandSource _source;
    private readonly TimeProvider _time;

    public PriceBandCheck(IPriceBandSource source, TimeProvider? time = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    // Runs right after PriceCollarCheck (300). The order matters: the
    // collar is the always-on static envelope; the venue band is the
    // tighter authoritative one — having both run lets a sustained
    // band/collar disagreement become observable (collar rejects with
    // RiskRejectCodes inferred from check Name vs band rejects with
    // RiskRejectCodes.PriceBand).
    public int Order => 305;
    public string Name => "price_band";

    public RiskDecision Check(RiskContext ctx)
    {
        if (!ctx.Price.HasValue) return RiskDecision.Approve; // market order

        if (!_source.TryGetBand(ctx.Symbol, out var band))
        {
            MetricsRegistry.PriceBandBypassedNoBand.Add(1,
                new KeyValuePair<string, object?>("symbol", ctx.Symbol));
            return RiskDecision.Approve;
        }

        // Age sampling: the histogram covers every consulted band,
        // not just the rejecting ones. p99 climbing on a symbol is
        // the early-warning signal that the venue throttled / stopped
        // re-publishing — same alert posture as RefPriceStalenessSeconds.
        var age = (_time.GetUtcNow() - band.AsOfUtc).TotalSeconds;
        MetricsRegistry.PriceBandAgeSeconds.Record(Math.Max(0d, age),
            new KeyValuePair<string, object?>("symbol", ctx.Symbol));

        var price = ctx.Price.Value;
        if (price < band.Lower)
        {
            MetricsRegistry.PriceBandRejects.Add(1,
                new KeyValuePair<string, object?>("symbol", ctx.Symbol),
                new KeyValuePair<string, object?>("side", ctx.Side.ToString()),
                new KeyValuePair<string, object?>("reason", "below"));
            return RiskDecision.Reject(
                RiskRejectCodes.PriceBand,
                $"price {price} below venue band [{band.Lower:0.####}, {band.Upper:0.####}] asOf {band.AsOfUtc:O}");
        }

        if (price > band.Upper)
        {
            MetricsRegistry.PriceBandRejects.Add(1,
                new KeyValuePair<string, object?>("symbol", ctx.Symbol),
                new KeyValuePair<string, object?>("side", ctx.Side.ToString()),
                new KeyValuePair<string, object?>("reason", "above"));
            return RiskDecision.Reject(
                RiskRejectCodes.PriceBand,
                $"price {price} above venue band [{band.Lower:0.####}, {band.Upper:0.####}] asOf {band.AsOfUtc:O}");
        }

        return RiskDecision.Approve;
    }
}
