namespace B3.Trading.Domain;

public enum OrderSide
{
    Buy,
    Sell,
}

public enum OrderType
{
    Limit,
    Market,
}

public enum OrderStatus
{
    PendingNew,
    Working,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
}

/// <summary>
/// Working order owned by a single end-client. Quantity / status mutate as
/// EntryPoint ExecutionReports flow back from the exchange. Persistence is
/// out-of-scope for the bootstrap; v1 is ephemeral and re-derived from ER
/// replay on (re)connect.
/// </summary>
public sealed class Order
{
    public Order(string clOrdId, EndClientId owner, string symbol, OrderSide side, OrderType type, long quantity, decimal? price)
    {
        ClOrdId = clOrdId;
        Owner = owner;
        Symbol = symbol;
        Side = side;
        Type = type;
        Quantity = quantity;
        Price = price;
        LeavesQuantity = quantity;
        Status = OrderStatus.PendingNew;
    }

    public string ClOrdId { get; }
    public EndClientId Owner { get; }
    public string Symbol { get; }
    public OrderSide Side { get; }
    public OrderType Type { get; }
    public long Quantity { get; }
    public decimal? Price { get; }
    public long LeavesQuantity { get; private set; }
    public long CumulativeQuantity { get; private set; }
    public OrderStatus Status { get; private set; }

    public void ApplyFill(long fillQty)
    {
        if (fillQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(fillQty));
        if (fillQty > LeavesQuantity)
            throw new InvalidOperationException("Fill exceeds leaves quantity.");

        CumulativeQuantity += fillQty;
        LeavesQuantity -= fillQty;
        Status = LeavesQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
    }

    public void MarkWorking() => Status = OrderStatus.Working;
    public void MarkCancelled() => Status = OrderStatus.Cancelled;
    public void MarkRejected() => Status = OrderStatus.Rejected;
}
