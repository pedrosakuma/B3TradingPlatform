using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application;

/// <summary>
/// Q2.3 (#270). Per-fill fee breakdown produced by
/// <see cref="IFeeCalculator"/>. All amounts are in BRL (single-currency
/// in v0) and rounded to 2 decimal places using
/// <see cref="MidpointRounding.AwayFromZero"/> (currency convention).
/// <see cref="Total"/> equals the rounded sum of the three components.
/// </summary>
public readonly record struct FeeBreakdown(
    decimal Brokerage,
    decimal Emolumentos,
    decimal Liquidacao,
    decimal Total);

/// <summary>
/// Q2.3 (#270). Pure function from (symbol, side, fill quantity, fill
/// price) to a <see cref="FeeBreakdown"/>. Deliberately stateless and
/// side-effect-free so the calculator can be exercised in unit tests
/// without any infrastructure wiring.
///
/// <para>
/// Side is included in the signature for forward-compat: the v0
/// schedule does not split per side, but exchange / clearing fee
/// schedules elsewhere do (e.g. liquidity-adding rebates), and exposing
/// it here means a future implementation does not require a breaking
/// change to the call sites.
/// </para>
/// </summary>
public interface IFeeCalculator
{
    FeeBreakdown Compute(string symbol, OrderSide side, long fillQuantity, decimal fillPrice);
}

/// <summary>
/// Q2.3 (#270). Default <see cref="IFeeCalculator"/> implementation:
/// <c>notional = fillQuantity * fillPrice</c>; brokerage is
/// <c>max(notional * brokerageBps / 10_000, brokerageMin)</c>;
/// emolumentos and liquidação are flat <c>notional * bps / 10_000</c>.
/// Each component is rounded to 2 decimal places with
/// <see cref="MidpointRounding.AwayFromZero"/> before being summed.
///
/// <para>
/// Per-symbol overrides come from <see cref="FeeOptions.Overrides"/>:
/// the lookup is exact-match (symbol comparison is case-sensitive), and
/// each <see cref="FeeSymbolOverride"/> field is merged independently
/// onto the defaults — a missing field inherits.
/// </para>
///
/// <para>
/// The calculator captures <see cref="IOptionsMonitor{TOptions}"/> at
/// construction and reads <c>CurrentValue</c> on every call rather than
/// snapshotting at construction time, so configuration hot-reload
/// propagates to the very next fill (no service restart).
/// </para>
/// </summary>
public sealed class BpsFeeCalculator : IFeeCalculator
{
    private readonly IOptionsMonitor<FeeOptions> _options;

    public BpsFeeCalculator(IOptionsMonitor<FeeOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public FeeBreakdown Compute(string symbol, OrderSide side, long fillQuantity, decimal fillPrice)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (fillQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(fillQuantity), "fillQuantity must be > 0");
        if (fillPrice < 0m)
            throw new ArgumentOutOfRangeException(nameof(fillPrice), "fillPrice must be >= 0");
        _ = side;

        var opts = _options.CurrentValue;
        var (brokerageBps, brokerageMin, emolBps, liqBps) = ResolveSchedule(opts, symbol);

        var notional = fillQuantity * fillPrice;
        var brokerageRaw = notional * brokerageBps / 10_000m;
        var brokerage = Round(Math.Max(brokerageRaw, brokerageMin));
        var emolumentos = Round(notional * emolBps / 10_000m);
        var liquidacao = Round(notional * liqBps / 10_000m);
        var total = brokerage + emolumentos + liquidacao;
        return new FeeBreakdown(brokerage, emolumentos, liquidacao, total);
    }

    private static (decimal brokerageBps, decimal brokerageMin, decimal emolBps, decimal liqBps)
        ResolveSchedule(FeeOptions opts, string symbol)
    {
        var brokerageBps = opts.BrokerageBps;
        var brokerageMin = opts.BrokerageMin;
        var emolBps = opts.EmolumentosBps;
        var liqBps = opts.LiquidacaoBps;

        if (opts.Overrides is { Count: > 0 })
        {
            for (var i = 0; i < opts.Overrides.Count; i++)
            {
                var ov = opts.Overrides[i];
                if (!string.Equals(ov.Symbol, symbol, StringComparison.Ordinal)) continue;
                if (ov.BrokerageBps is { } bb) brokerageBps = bb;
                if (ov.BrokerageMin is { } bm) brokerageMin = bm;
                if (ov.EmolumentosBps is { } eb) emolBps = eb;
                if (ov.LiquidacaoBps is { } lb) liqBps = lb;
                break;
            }
        }
        return (brokerageBps, brokerageMin, emolBps, liqBps);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
