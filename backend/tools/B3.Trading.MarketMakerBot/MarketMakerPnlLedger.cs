namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Process-local, gross/pre-fee position and P&amp;L ledger for the market
/// maker's own known fills. State is intentionally not persisted.
/// </summary>
public sealed class MarketMakerPnlLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MutablePosition> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, ulong> _orderCumQty = new();
    private readonly Dictionary<ExecutionIdentity, ExecutionSignature> _executions = new();

    public FillApplyResult Apply(OwnFill fill)
    {
        lock (_gate)
        {
            var identity = new ExecutionIdentity(fill.ClOrdId, fill.TradeId);
            var signature = new ExecutionSignature(fill.Price, fill.Quantity, fill.CumQty, fill.LeavesQty);
            if (_executions.TryGetValue(identity, out var existing))
            {
                return existing == signature
                    ? new(FillApplyStatus.Duplicate, "execution identity already applied")
                    : new(FillApplyStatus.Inconsistent, "execution identity was reused with different fill data");
            }

            var validationError = Validate(fill, out var nextCumQty);
            if (validationError is not null)
                return validationError.Value;

            var signedQuantity = checked((long)fill.Quantity) * (fill.IsBuy ? 1L : -1L);
            var isNewPosition = !_positions.TryGetValue(fill.Symbol, out var position);
            position ??= new MutablePosition();

            try
            {
                ApplyWeightedAverageCost(position, signedQuantity, fill.Price);
            }
            catch (OverflowException)
            {
                return new(FillApplyStatus.Invalid, "position or P&L arithmetic overflow");
            }
            if (isNewPosition)
                _positions.Add(fill.Symbol, position);
            _orderCumQty[fill.ClOrdId] = nextCumQty;
            _executions.Add(identity, signature);
            return new(FillApplyStatus.Applied, null);
        }
    }

    public bool TryGetSnapshot(string symbol, out MarketMakerPnlSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_positions.TryGetValue(symbol, out var position))
            {
                snapshot = ToSnapshot(symbol, position);
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    public IReadOnlyList<MarketMakerPnlSnapshot> SnapshotAll()
    {
        lock (_gate)
        {
            return _positions
                .Select(kv => ToSnapshot(kv.Key, kv.Value))
                .OrderBy(snapshot => snapshot.Symbol, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private FillApplyResult? Validate(OwnFill fill, out ulong nextCumQty)
    {
        nextCumQty = 0;
        if (string.IsNullOrWhiteSpace(fill.Symbol))
            return new(FillApplyStatus.Invalid, "symbol is missing");
        if (fill.OrderQuantity <= 0)
            return new(FillApplyStatus.Invalid, "known order quantity is not positive");
        if (!fill.HasValidOrderStatus)
            return new(FillApplyStatus.Invalid, "OrderTrade status is neither PartiallyFilled nor Filled");
        if (fill.TradeId == 0)
            return new(FillApplyStatus.Invalid, "TradeId is zero");
        if (fill.Price <= 0m)
            return new(FillApplyStatus.Invalid, "LastPx is not positive");
        if (fill.Quantity == 0 || fill.Quantity > long.MaxValue)
            return new(FillApplyStatus.Invalid, "LastQty is outside the supported range");

        var orderQuantity = (ulong)fill.OrderQuantity;
        if (fill.Quantity > orderQuantity)
            return new(FillApplyStatus.Invalid, "LastQty exceeds the known order quantity");

        var previousCumQty = _orderCumQty.GetValueOrDefault(fill.ClOrdId);
        if (ulong.MaxValue - previousCumQty < fill.Quantity)
            return new(FillApplyStatus.Invalid, "cumulative quantity overflow");
        nextCumQty = previousCumQty + fill.Quantity;

        if (fill.CumQty is { } reportedCumQty && reportedCumQty != nextCumQty)
        {
            return new(FillApplyStatus.Inconsistent,
                $"CumQty {reportedCumQty} does not equal prior CumQty {previousCumQty} plus LastQty {fill.Quantity}");
        }

        if (nextCumQty > orderQuantity)
            return new(FillApplyStatus.Inconsistent, "cumulative quantity exceeds the known order quantity");

        if (fill.LeavesQty is { } leavesQty &&
            (leavesQty > orderQuantity || nextCumQty != orderQuantity - leavesQty))
        {
            return new(FillApplyStatus.Inconsistent,
                "CumQty/LastQty and LeavesQty do not reconcile to the known order quantity");
        }

        if (fill.IsOrderFilled && nextCumQty != orderQuantity)
            return new(FillApplyStatus.Inconsistent, "Filled status does not reconcile to the known order quantity");
        if (!fill.IsOrderFilled && nextCumQty == orderQuantity)
            return new(FillApplyStatus.Inconsistent, "PartiallyFilled status reports the full known order quantity");

        return null;
    }

    private static void ApplyWeightedAverageCost(MutablePosition position, long signedQuantity, decimal price)
    {
        if (position.Quantity == 0 || Math.Sign(position.Quantity) == Math.Sign(signedQuantity))
        {
            var oldAbsoluteQuantity = Math.Abs(position.Quantity);
            var addedAbsoluteQuantity = Math.Abs(signedQuantity);
            var newAbsoluteQuantity = checked(oldAbsoluteQuantity + addedAbsoluteQuantity);
            var increasedQuantity = checked(position.Quantity + signedQuantity);
            var newAverageCost =
                ((position.AverageCost * oldAbsoluteQuantity) + (price * addedAbsoluteQuantity)) /
                newAbsoluteQuantity;
            position.Quantity = increasedQuantity;
            position.AverageCost = newAverageCost;
            return;
        }

        var closingQuantity = Math.Min(Math.Abs(position.Quantity), Math.Abs(signedQuantity));
        position.RealizedPnl += position.Quantity > 0
            ? (price - position.AverageCost) * closingQuantity
            : (position.AverageCost - price) * closingQuantity;

        var newQuantity = checked(position.Quantity + signedQuantity);
        if (newQuantity == 0)
        {
            position.Quantity = 0;
            position.AverageCost = 0m;
        }
        else if (Math.Sign(newQuantity) != Math.Sign(position.Quantity))
        {
            position.Quantity = newQuantity;
            position.AverageCost = price;
        }
        else
        {
            position.Quantity = newQuantity;
        }
    }

    private static MarketMakerPnlSnapshot ToSnapshot(string symbol, MutablePosition position) =>
        new(symbol, position.Quantity, position.AverageCost, position.RealizedPnl);

    private sealed class MutablePosition
    {
        public long Quantity;
        public decimal AverageCost;
        public decimal RealizedPnl;
    }

    private readonly record struct ExecutionIdentity(ulong ClOrdId, ulong TradeId);
    private readonly record struct ExecutionSignature(decimal Price, ulong Quantity, ulong? CumQty, ulong? LeavesQty);
}

public readonly record struct OwnFill(
    ulong ClOrdId,
    ulong TradeId,
    string Symbol,
    bool IsBuy,
    long OrderQuantity,
    decimal Price,
    ulong Quantity,
    ulong? CumQty,
    ulong? LeavesQty,
    bool IsOrderFilled,
    bool HasValidOrderStatus = true);

public readonly record struct FillApplyResult(FillApplyStatus Status, string? Reason);

public enum FillApplyStatus
{
    Applied,
    Duplicate,
    Invalid,
    Inconsistent,
}

public readonly record struct MarketMakerPnlSnapshot(
    string Symbol,
    long Position,
    decimal AverageCost,
    decimal RealizedPnl)
{
    public decimal UnrealizedPnl(decimal mark) => (mark - AverageCost) * Position;
}
