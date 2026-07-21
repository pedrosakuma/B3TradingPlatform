namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Pure, deterministic bid/ask pricing for the market maker. No
/// randomness: given the same <see cref="InstrumentConfig"/> and side,
/// this always produces the same quote. Anchors on the configured
/// <see cref="InstrumentConfig.RefPrice"/> for now; a follow-up will
/// swap that anchor for the live book mid once the bot consumes market
/// data (see issue #683, item "mm-marketdata-conn").
/// </summary>
public static class QuoteCalculator
{
    /// <summary>Computes the resting price for <paramref name="isBuy"/>,
    /// symmetric around <see cref="InstrumentConfig.RefPrice"/> by
    /// <see cref="InstrumentConfig.SpreadTicks"/> ticks, rounded to
    /// <see cref="InstrumentConfig.TickSize"/>.</summary>
    public static decimal ComputeQuotePrice(InstrumentConfig instrument, bool isBuy)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        var offset = instrument.SpreadTicks * instrument.TickSize;
        var raw = isBuy ? instrument.RefPrice - offset : instrument.RefPrice + offset;
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
