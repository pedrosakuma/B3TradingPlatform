namespace B3.Trading.SimulatorBot;

/// <summary>
/// Stateless price/quantity generator. Decoupled from the worker so
/// it's deterministic-testable with a seeded <see cref="Random"/>.
/// </summary>
public static class OrderPattern
{
    /// <summary>
    /// Draws the next order for an instrument. Returns <c>null</c> when
    /// the in-flight cap is reached for this symbol.
    /// </summary>
    /// <param name="rng">Seeded for deterministic tests.</param>
    /// <param name="instr">Instrument config (ref-price, tick, lot).</param>
    /// <param name="crossProbability">0..1; chance the order crosses
    /// the bot's own ref-price symmetric quote (becoming an aggressive
    /// taker rather than passive resting liquidity).</param>
    /// <param name="inFlight">Current open count for the symbol.</param>
    /// <param name="cap">Max in-flight per symbol.</param>
    public static OrderDraft? Next(Random rng, InstrumentConfig instr,
        double crossProbability, int inFlight, int cap)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(instr);
        if (inFlight >= cap) return null;

        var isBuy = rng.NextDouble() < 0.5;
        var lots = rng.Next(instr.MinLots, instr.MaxLots + 1);
        var quantity = checked(lots * instr.LotSize);

        // Resting orders sit just inside the spread (passive). Crossing
        // orders sit on the OPPOSITE side of mid — a buy crosses by
        // pricing above ref, a sell by pricing below — so they have a
        // chance of immediate match against working liquidity.
        var crosses = rng.NextDouble() < crossProbability;
        decimal raw;
        if (crosses)
        {
            // ±0.1% across mid (always crossing direction).
            var bps = (decimal)(rng.NextDouble() * 0.001 + 0.0005);
            raw = isBuy ? instr.RefPrice * (1m + bps) : instr.RefPrice * (1m - bps);
        }
        else
        {
            // ±0.5% inside spread (resting on own side of mid).
            var bps = (decimal)(rng.NextDouble() * 0.005);
            raw = isBuy ? instr.RefPrice * (1m - bps) : instr.RefPrice * (1m + bps);
        }

        var price = RoundToTick(raw, instr.TickSize);
        if (price <= 0m) return null; // pathological config; skip silently.
        return new OrderDraft(instr.Symbol, instr.SecurityId, isBuy, quantity, price);
    }

    public static decimal RoundToTick(decimal value, decimal tickSize)
    {
        if (tickSize <= 0m) throw new ArgumentOutOfRangeException(nameof(tickSize));
        var ticks = Math.Round(value / tickSize, MidpointRounding.AwayFromZero);
        return ticks * tickSize;
    }
}

public readonly record struct OrderDraft(string Symbol, ulong SecurityId, bool IsBuy, long Quantity, decimal Price);
