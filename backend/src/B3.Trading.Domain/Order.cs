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
    public Order(
        ulong clOrdId,
        EndClientId owner,
        string symbol,
        ulong securityId,
        OrderSide side,
        OrderType type,
        long quantity,
        decimal? price,
        string firmId = "DEFAULT",
        ulong? parentAlgoId = null,
        int? algoSliceSeq = null)
    {
        if (clOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(clOrdId), "ClOrdID cannot be zero (reserved as null sentinel by EntryPoint).");
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("FirmId required.", nameof(firmId));
        if (parentAlgoId is 0)
            throw new ArgumentOutOfRangeException(nameof(parentAlgoId), "ParentAlgoId cannot be zero (reserved as null sentinel).");
        if ((parentAlgoId is null) != (algoSliceSeq is null))
            throw new ArgumentException("ParentAlgoId and AlgoSliceSeq must be set together (both null = manual order; both set = algo child).");
        if (algoSliceSeq is < 0)
            throw new ArgumentOutOfRangeException(nameof(algoSliceSeq));
        ClOrdId = clOrdId;
        Owner = owner;
        Symbol = symbol;
        SecurityId = securityId;
        Side = side;
        Type = type;
        Quantity = quantity;
        Price = price;
        FirmId = firmId;
        ParentAlgoId = parentAlgoId;
        AlgoSliceSeq = algoSliceSeq;
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
    /// <summary>
    /// When set, this order is a child slice produced by an
    /// <c>AlgoEngine</c> on behalf of the parent <see cref="Algo"/> with
    /// id <see cref="ParentAlgoId"/> and slice index <see cref="AlgoSliceSeq"/>.
    /// Manual orders submitted via <c>POST /orders</c> leave both fields
    /// <c>null</c>. The pair is set together or both <c>null</c> — never
    /// one without the other (RFC §4.2).
    /// </summary>
    public ulong? ParentAlgoId { get; }
    public int? AlgoSliceSeq { get; }
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

    /// <summary>
    /// Cumulative-quantity-driven fill application. Returns the delta that
    /// was applied (0 when the incoming <paramref name="newCumulativeQty"/>
    /// is stale/duplicate). Designed to be safe under ER replay and
    /// out-of-order delivery: only ever advances forward, never throws,
    /// and preserves a terminal <see cref="OrderStatus.Cancelled"/> /
    /// <see cref="OrderStatus.Rejected"/> when a "late" fill arrives after
    /// the terminal ER (the exchange may legitimately deliver a fill that
    /// happened pre-cancel after the cancel-ack).
    ///
    /// <para>
    /// Overfill (newCumQty &gt; Quantity) is permitted: leaves clamps at 0
    /// and the field still advances to whatever the exchange reports,
    /// because the WAL replay must remain total — throwing here would
    /// poison recovery for any persisted ER stream containing an overfill.
    /// </para>
    /// </summary>
    public long ApplyCumulativeFill(long newCumulativeQty)
    {
        if (newCumulativeQty <= CumulativeQuantity)
            return 0;

        var delta = newCumulativeQty - CumulativeQuantity;
        CumulativeQuantity = newCumulativeQty;
        LeavesQuantity = Math.Max(0, Quantity - newCumulativeQty);

        // Status only advances; never regresses out of a terminal state.
        if (Status is not (OrderStatus.Cancelled or OrderStatus.Rejected))
            Status = LeavesQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

        return delta;
    }

    public void MarkWorking()
    {
        // Idempotency: New ER may be re-delivered after reconnect. Only the
        // PendingNew→Working transition is meaningful; later ERs (including
        // any that re-state New) must not regress an already-fillable
        // order back to Working.
        if (Status == OrderStatus.PendingNew)
            Status = OrderStatus.Working;
    }

    public void MarkCancelled()
    {
        // Once filled, the order can't be cancelled — a stale Cancelled ER
        // delivered after the final fill would otherwise regress status.
        if (Status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Cancelled)
            return;
        Status = OrderStatus.Cancelled;
    }

    public void MarkRejected()
    {
        // Rejection is only valid before any fill. A stale Reject after a
        // partial/full fill must be ignored.
        if (Status is OrderStatus.Filled or OrderStatus.PartiallyFilled or OrderStatus.Rejected or OrderStatus.Cancelled)
            return;
        Status = OrderStatus.Rejected;
    }

    /// <summary>
    /// Reconstructs an order from snapshot data. For persistence recovery
    /// only — bypasses the state-machine invariants because the snapshot
    /// was, by construction, produced from a sequence of valid mutations.
    /// </summary>
    internal static Order Hydrate(
        ulong clOrdId, EndClientId owner, string symbol, ulong securityId, OrderSide side, OrderType type,
        long quantity, decimal? price, long leaves, long cumQty, OrderStatus status, string firmId = "DEFAULT",
        ulong? parentAlgoId = null, int? algoSliceSeq = null)
    {
        var o = new Order(clOrdId, owner, symbol, securityId, side, type, quantity, price, firmId, parentAlgoId, algoSliceSeq);
        o.LeavesQuantity = leaves;
        o.CumulativeQuantity = cumQty;
        o.Status = status;
        return o;
    }
}
