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
    /// <summary>
    /// <paramref name="firmId"/> is the FIXP session this order belongs to.
    /// Required by the gateway to route cancel/replace requests to the right
    /// upstream <c>EntryPointClient</c> when the host is configured for
    /// multiple firms. Default <c>"DEFAULT"</c> exists only to keep older
    /// unit tests terse; production call sites always pass an explicit firm.
    /// </summary>
    public Order(ulong clOrdId, EndClientId owner, string symbol, ulong securityId, OrderSide side, OrderType type, long quantity, decimal? price, string firmId = "DEFAULT")
    {
        if (clOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(clOrdId), "ClOrdID cannot be zero (reserved as null sentinel by EntryPoint).");
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("FirmId required.", nameof(firmId));
        ClOrdId = clOrdId;
        Owner = owner;
        Symbol = symbol;
        SecurityId = securityId;
        Side = side;
        Type = type;
        Quantity = quantity;
        Price = price;
        FirmId = firmId;
        LeavesQuantity = quantity;
        Status = OrderStatus.PendingNew;
    }

    public ulong ClOrdId { get; }
    public EndClientId Owner { get; }
    public string Symbol { get; }
    public ulong SecurityId { get; }
    public string FirmId { get; }
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

    /// <summary>
    /// Reconstructs an order from snapshot data. For persistence recovery
    /// only — bypasses the state-machine invariants because the snapshot
    /// was, by construction, produced from a sequence of valid mutations.
    /// </summary>
    internal static Order Hydrate(
        ulong clOrdId, EndClientId owner, string symbol, ulong securityId, OrderSide side, OrderType type,
        long quantity, decimal? price, long leaves, long cumQty, OrderStatus status, string firmId = "DEFAULT")
    {
        var o = new Order(clOrdId, owner, symbol, securityId, side, type, quantity, price, firmId);
        o.LeavesQuantity = leaves;
        o.CumulativeQuantity = cumQty;
        o.Status = status;
        return o;
    }
}
