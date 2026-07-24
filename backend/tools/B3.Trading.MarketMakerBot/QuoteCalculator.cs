namespace B3.Trading.MarketMakerBot;

public enum QuoteReferenceSource
{
    ConfiguredRefPrice,
    LiveMarketData,
    Explicit,
}

public enum QuoteSuppressionReason
{
    None,
    InstrumentDelisted,
    NonPositivePrice,
}

/// <summary>
/// Immutable inputs to one side's pricing decision. The currently inactive
/// strategy fields are explicit so inventory skew and adaptive spread can
/// change this seam without creating a second pricing path.
/// </summary>
public readonly record struct QuoteInputs(
    bool IsBuy,
    decimal ReferencePrice,
    QuoteReferenceSource ReferenceSource,
    decimal InventoryMidShift,
    decimal ConfiguredHalfSpread,
    decimal EffectiveHalfSpread,
    decimal TickSize,
    QuoteSuppressionReason SuppressionReason = QuoteSuppressionReason.None);

/// <summary>Pure result consumed by both new-order submission and reactive repricing.</summary>
public readonly record struct QuoteDecision(
    bool ShouldQuote,
    decimal? Price,
    decimal ReferencePrice,
    QuoteReferenceSource ReferenceSource,
    decimal InventoryMidShift,
    decimal ConfiguredHalfSpread,
    decimal EffectiveHalfSpread,
    QuoteSuppressionReason SuppressionReason);

/// <summary>
/// Pure, deterministic bid/ask pricing for the market maker. No
/// randomness: the same immutable inputs always produce the same decision.
/// </summary>
public static class QuoteCalculator
{
    public static QuoteDecision Decide(QuoteInputs inputs)
    {
        if (inputs.SuppressionReason != QuoteSuppressionReason.None)
            return Suppressed(inputs, inputs.SuppressionReason);

        var price = CalculateRoundedPrice(inputs);
        return price > 0m
            ? new QuoteDecision(
                true,
                price,
                inputs.ReferencePrice,
                inputs.ReferenceSource,
                inputs.InventoryMidShift,
                inputs.ConfiguredHalfSpread,
                inputs.EffectiveHalfSpread,
                QuoteSuppressionReason.None)
            : Suppressed(inputs, QuoteSuppressionReason.NonPositivePrice);
    }

    /// <summary>Computes the resting price for <paramref name="isBuy"/>,
    /// symmetric around <see cref="InstrumentConfig.RefPrice"/> by
    /// <see cref="InstrumentConfig.SpreadTicks"/> ticks, rounded to
    /// <see cref="InstrumentConfig.TickSize"/>.</summary>
    public static decimal ComputeQuotePrice(InstrumentConfig instrument, bool isBuy) =>
        ComputeQuotePrice(instrument, isBuy, instrument?.RefPrice ?? 0m);

    /// <summary>Same as <see cref="ComputeQuotePrice(InstrumentConfig, bool)"/>
    /// but anchored on <paramref name="referencePrice"/> instead of the
    /// instrument's static <see cref="InstrumentConfig.RefPrice"/>. Kept as
    /// a compatibility seam for callers/tests that only need the final
    /// price; the worker consumes <see cref="Decide"/> instead.</summary>
    public static decimal ComputeQuotePrice(InstrumentConfig instrument, bool isBuy, decimal referencePrice)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        var halfSpread = instrument.SpreadTicks * instrument.TickSize;
        return CalculateRoundedPrice(new QuoteInputs(
            isBuy,
            referencePrice,
            QuoteReferenceSource.Explicit,
            InventoryMidShift: 0m,
            ConfiguredHalfSpread: halfSpread,
            EffectiveHalfSpread: halfSpread,
            instrument.TickSize));
    }

    /// <summary>Quote size in shares: <see cref="InstrumentConfig.QuoteLots"/>
    /// times <see cref="InstrumentConfig.LotSize"/>.</summary>
    public static long QuoteQuantity(InstrumentConfig instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return checked(instrument.QuoteLots * instrument.LotSize);
    }

    public static decimal RoundToTick(decimal value, decimal tickSize)
    {
        if (tickSize <= 0m) throw new ArgumentOutOfRangeException(nameof(tickSize));
        var ticks = Math.Round(value / tickSize, MidpointRounding.AwayFromZero);
        return ticks * tickSize;
    }

    private static QuoteDecision Suppressed(QuoteInputs inputs, QuoteSuppressionReason reason) =>
        new(
            false,
            null,
            inputs.ReferencePrice,
            inputs.ReferenceSource,
            inputs.InventoryMidShift,
            inputs.ConfiguredHalfSpread,
            inputs.EffectiveHalfSpread,
            reason);

    private static decimal CalculateRoundedPrice(QuoteInputs inputs)
    {
        var shiftedMid = inputs.ReferencePrice + inputs.InventoryMidShift;
        var raw = inputs.IsBuy
            ? shiftedMid - inputs.EffectiveHalfSpread
            : shiftedMid + inputs.EffectiveHalfSpread;
        return RoundToTick(raw, inputs.TickSize);
    }
}
