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

    [Fact]
    public void ComputeQuotePrice_WithExplicitReferencePrice_AnchorsOnThatInsteadOfConfiguredRefPrice()
    {
        // Config RefPrice is 30, but a live market-data anchor of 50
        // should win when explicitly passed — this is the overload the
        // worker uses once market data is flowing.
        var instr = Instrument(refPrice: 30m, tickSize: 0.01m, spreadTicks: 5);
        Assert.Equal(49.95m, QuoteCalculator.ComputeQuotePrice(instr, isBuy: true, referencePrice: 50m));
        Assert.Equal(50.05m, QuoteCalculator.ComputeQuotePrice(instr, isBuy: false, referencePrice: 50m));
    }

    [Fact]
    public void ComputeQuotePrice_TwoArgOverload_DelegatesToConfiguredRefPrice()
    {
        var instr = Instrument(refPrice: 30m, tickSize: 0.01m, spreadTicks: 5);
        Assert.Equal(QuoteCalculator.ComputeQuotePrice(instr, isBuy: true, instr.RefPrice),
            QuoteCalculator.ComputeQuotePrice(instr, isBuy: true));
    }

    [Fact]
    public void Decide_ReturnsCompleteImmutablePricingDecision()
    {
        var inputs = new QuoteInputs(
            IsBuy: true,
            ReferencePrice: 30m,
            QuoteReferenceSource.LiveMarketData,
            InventoryMidShift: 0m,
            ConfiguredHalfSpread: 0.05m,
            EffectiveHalfSpread: 0.05m,
            TickSize: 0.01m);

        var decision = QuoteCalculator.Decide(inputs);

        Assert.True(decision.ShouldQuote);
        Assert.Equal(29.95m, decision.Price);
        Assert.Equal(30m, decision.ReferencePrice);
        Assert.Equal(QuoteReferenceSource.LiveMarketData, decision.ReferenceSource);
        Assert.Equal(0m, decision.InventoryMidShift);
        Assert.Equal(0.05m, decision.ConfiguredHalfSpread);
        Assert.Equal(0.05m, decision.EffectiveHalfSpread);
        Assert.Equal(QuoteSuppressionReason.None, decision.SuppressionReason);
    }

    [Fact]
    public void Decide_PreSuppressedContext_PreservesContextWithoutPrice()
    {
        var decision = QuoteCalculator.Decide(new QuoteInputs(
            IsBuy: false,
            ReferencePrice: 30m,
            QuoteReferenceSource.ConfiguredRefPrice,
            InventoryMidShift: 0m,
            ConfiguredHalfSpread: 0.05m,
            EffectiveHalfSpread: 0.05m,
            TickSize: 0.01m,
            QuoteSuppressionReason.InstrumentDelisted));

        Assert.False(decision.ShouldQuote);
        Assert.Null(decision.Price);
        Assert.Equal(QuoteSuppressionReason.InstrumentDelisted, decision.SuppressionReason);
        Assert.Equal(QuoteReferenceSource.ConfiguredRefPrice, decision.ReferenceSource);
    }

    [Fact]
    public void Decide_NonPositiveRoundedPrice_IsSuppressed()
    {
        var decision = QuoteCalculator.Decide(new QuoteInputs(
            IsBuy: true,
            ReferencePrice: 0.01m,
            QuoteReferenceSource.ConfiguredRefPrice,
            InventoryMidShift: 0m,
            ConfiguredHalfSpread: 0.05m,
            EffectiveHalfSpread: 0.05m,
            TickSize: 0.01m));

        Assert.False(decision.ShouldQuote);
        Assert.Null(decision.Price);
        Assert.Equal(QuoteSuppressionReason.NonPositivePrice, decision.SuppressionReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decide_DefaultInputs_MatchesConvenienceOverload(bool isBuy)
    {
        var instrument = Instrument();
        var halfSpread = instrument.SpreadTicks * instrument.TickSize;
        var decision = QuoteCalculator.Decide(new QuoteInputs(
            isBuy,
            instrument.RefPrice,
            QuoteReferenceSource.ConfiguredRefPrice,
            InventoryMidShift: 0m,
            ConfiguredHalfSpread: halfSpread,
            EffectiveHalfSpread: halfSpread,
            instrument.TickSize));

        Assert.Equal(QuoteCalculator.ComputeQuotePrice(instrument, isBuy), decision.Price);
    }
}
