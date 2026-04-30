namespace B3.Trading.Domain;

/// <summary>
/// Cumulative net position for a single end-client / symbol pair, derived
/// from the ER stream. Long quantities are positive, short are negative.
/// </summary>
public sealed class Position
{
    public Position(EndClientId owner, string symbol)
    {
        Owner = owner;
        Symbol = symbol;
    }

    public EndClientId Owner { get; }
    public string Symbol { get; }
    public long NetQuantity { get; private set; }
    public decimal AverageEntryPrice { get; private set; }

    public void ApplyFill(OrderSide side, long quantity, decimal price)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        var signed = side == OrderSide.Buy ? quantity : -quantity;
        var newQty = NetQuantity + signed;

        // Average price: only recompute when growing the position in the same
        // direction; offsetting fills keep the prior average until a flip.
        if (NetQuantity == 0 || Math.Sign(NetQuantity) == Math.Sign(signed))
        {
            var prior = (decimal)Math.Abs(NetQuantity);
            var added = (decimal)quantity;
            var total = prior + added;
            AverageEntryPrice = total == 0 ? 0m : ((AverageEntryPrice * prior) + (price * added)) / total;
        }
        else if (Math.Sign(newQty) != Math.Sign(NetQuantity) && newQty != 0)
        {
            // Position flipped past zero: reset average to fill price.
            AverageEntryPrice = price;
        }

        NetQuantity = newQty;
        if (NetQuantity == 0)
            AverageEntryPrice = 0m;
    }
}
