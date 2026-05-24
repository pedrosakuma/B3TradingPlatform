using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Accounting;

/// <summary>
/// Holds the per-end-client and per-firm sliding ledgers used by
/// <see cref="Checks.RollingNotionalCheck"/>. Implements
/// <see cref="IRiskAccountant"/> so accepted submits feed both
/// ledgers.
/// </summary>
public sealed class RollingNotionalAccountant : IRiskAccountant
{
    private readonly SlidingWindowLedger _perEndClient;
    private readonly SlidingWindowLedger _perFirm;
    private readonly SlidingWindowLedger _perAlgo;
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly IReferencePrice _refPrice;
    private readonly IMarketValueCalculator _values;

    public RollingNotionalAccountant(
        IOptionsMonitor<RiskOptions> options,
        IReferencePrice refPrice,
        TimeProvider clock,
        IMarketValueCalculator? values = null)
    {
        _options = options;
        _refPrice = refPrice;
        _values = values ?? EquityMarketValueCalculator.Instance;
        _perEndClient = new SlidingWindowLedger(clock);
        _perFirm = new SlidingWindowLedger(clock);
        _perAlgo = new SlidingWindowLedger(clock);
    }

    public SlidingWindowLedger EndClientLedger => _perEndClient;
    public SlidingWindowLedger FirmLedger => _perFirm;

    /// <summary>
    /// #435. Per-(firm, parentAlgoId) ledger; keys are formatted
    /// <c>{firmId}:{parentAlgoId}</c>. Backs the per-algo throttle
    /// bucket in <see cref="Checks.RollingNotionalCheck"/>.
    /// </summary>
    public SlidingWindowLedger AlgoLedger => _perAlgo;

    /// <summary>
    /// #435. Stable composite key for the per-algo ledger so the
    /// check and the accountant agree on the bucket addressed.
    /// </summary>
    public static string AlgoKey(string firmId, ulong parentAlgoId) =>
        $"{firmId}:{parentAlgoId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Computes the notional that will be charged to the rolling
    /// ledger for an order. Limit orders use their own price; market
    /// orders fall back to <see cref="IReferencePrice.Lookup"/>. When
    /// no reference is available the notional is 0 and a bypass
    /// metric is incremented — fail-open posture matching the
    /// price-collar check.
    ///
    /// <para>
    /// OPT-B (#484): both branches go through
    /// <see cref="IMarketValueCalculator"/> so option flow charges
    /// the ledger the correct multiplier-adjusted notional (the
    /// rolling cap is in BRL, not contracts).
    /// </para>
    /// </summary>
    public decimal NotionalFor(RiskContext ctx)
    {
        if (ctx.Price is { } px) return _values.GetNotional(ctx.Symbol, px, ctx.Quantity);

        var lookup = _refPrice.Lookup(ctx.Symbol);
        if (lookup.Found && lookup.Price > 0m)
            return _values.GetNotional(ctx.Symbol, lookup.Price, ctx.Quantity);

        MetricsRegistry.RollingNotionalBypassedNoReference.Add(1,
            new KeyValuePair<string, object?>("symbol", ctx.Symbol));
        return 0m;
    }

    public TimeSpan Window => TimeSpan.FromSeconds(
        Math.Max(1, _options.CurrentValue.RollingNotional.WindowSeconds));

    public void RecordAccepted(RiskContext ctx)
    {
        var notional = NotionalFor(ctx);
        if (notional <= 0m) return;
        _perEndClient.Append(ctx.Owner.Value, notional);
        if (!string.IsNullOrWhiteSpace(ctx.FirmId))
            _perFirm.Append(ctx.FirmId, notional);
        if (ctx.ParentAlgoId is { } algoId && !string.IsNullOrWhiteSpace(ctx.FirmId))
            _perAlgo.Append(AlgoKey(ctx.FirmId, algoId), notional);
    }
}
