namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Pure, deterministic bid/ask pricing for the market maker. No
/// randomness: given the same reference price, instrument, and side,
/// this always produces the same quote.
/// </summary>
public static class QuoteCalculator
{
    /// <summary>Computes the resting price for <paramref name="isBuy"/>,
    /// symmetric around <see cref="InstrumentConfig.RefPrice"/> by
    /// <see cref="InstrumentConfig.SpreadTicks"/> ticks, rounded to
    /// <see cref="InstrumentConfig.TickSize"/>.</summary>
    public static decimal ComputeQuotePrice(InstrumentConfig instrument, bool isBuy) =>
        ComputeQuotePrice(instrument, isBuy, instrument?.RefPrice ?? 0m);

    /// <summary>Same as <see cref="ComputeQuotePrice(InstrumentConfig, bool)"/>
    /// but anchored on <paramref name="referencePrice"/> instead of the
    /// instrument's static <see cref="InstrumentConfig.RefPrice"/> — used
    /// by the worker to quote off the live market-data reference price
    /// once available (see <see cref="MarketPriceTracker"/>), falling
    /// back to the config value only before the first market-data update
    /// arrives or while the feed is disconnected.</summary>
    public static decimal ComputeQuotePrice(InstrumentConfig instrument, bool isBuy, decimal referencePrice)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        var offset = instrument.SpreadTicks * instrument.TickSize;
        var raw = isBuy ? referencePrice - offset : referencePrice + offset;
        return RoundToTick(raw, instrument.TickSize);
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
}
