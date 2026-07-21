using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class QuoteCalculatorTests
{
    private static InstrumentConfig Instrument(decimal refPrice = 30m, decimal tickSize = 0.01m,
        long lotSize = 100, int quoteLots = 2, int spreadTicks = 5) => new()
        {
            Symbol = "PETR4",
            SecurityId = 1UL,
            RefPrice = refPrice,
            TickSize = tickSize,
            LotSize = lotSize,
            QuoteLots = quoteLots,
            SpreadTicks = spreadTicks,
        };

    [Fact]
    public void ComputeQuotePrice_Buy_IsBelowRefPriceBySpread()
    {
        var instr = Instrument(refPrice: 30m, tickSize: 0.01m, spreadTicks: 5);
        Assert.Equal(29.95m, QuoteCalculator.ComputeQuotePrice(instr, isBuy: true));
    }

    [Fact]
    public void ComputeQuotePrice_Sell_IsAboveRefPriceBySpread()
    {
        var instr = Instrument(refPrice: 30m, tickSize: 0.01m, spreadTicks: 5);
        Assert.Equal(30.05m, QuoteCalculator.ComputeQuotePrice(instr, isBuy: false));
    }

    [Fact]
    public void ComputeQuotePrice_IsDeterministic()
    {
        var instr = Instrument();
        var a = QuoteCalculator.ComputeQuotePrice(instr, isBuy: true);
        var b = QuoteCalculator.ComputeQuotePrice(instr, isBuy: true);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeQuotePrice_RoundsToTick()
    {
        // RefPrice/spread chosen so the raw price is off-tick before rounding.
        var instr = Instrument(refPrice: 30.001m, tickSize: 0.01m, spreadTicks: 0);
        var price = QuoteCalculator.ComputeQuotePrice(instr, isBuy: true);
        Assert.Equal(30.00m, price);
    }

    [Fact]
    public void QuoteQuantity_IsLotsTimesLotSize()
    {
        var instr = Instrument(lotSize: 100, quoteLots: 3);
        Assert.Equal(300, QuoteCalculator.QuoteQuantity(instr));
    }

    [Fact]
    public void RoundToTick_ThrowsForNonPositiveTick()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuoteCalculator.RoundToTick(1m, 0m));
    }
}
