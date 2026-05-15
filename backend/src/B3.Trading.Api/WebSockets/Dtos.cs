using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Api.WebSockets;

/// <summary>Logical channel names exposed by the hub.</summary>
public static class Channels
{
    public const string OrdersMe = "orders.me";
    public const string ExecutionsMe = "executions.me";
    public const string PositionsMe = "positions.me";
    public const string AlgoMe = "algo.me";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { OrdersMe, ExecutionsMe, PositionsMe, AlgoMe };
}

/// <summary>Inbound command from a connected client.</summary>
public sealed record InboundCommand(string Type, string[]? Channels);

/// <summary>Outbound message envelope.</summary>
public sealed record OutboundMessage(string Type, string? Channel, long Seq, object? Data, string? Code = null, string? Message = null);

public sealed record OrderDto(
    string ClOrdId,
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    long LeavesQuantity,
    long CumulativeQuantity,
    decimal? Price,
    string Status,
    string? ParentAlgoId = null,
    int? AlgoSliceSeq = null,
    bool IsStale = false,
    string? StaleReason = null,
    DateTimeOffset? StaledAtUtc = null,
    /// <summary>Q1.1 (#253). Time-in-force; defaults to <c>"Day"</c>.</summary>
    string TimeInForce = "Day",
    /// <summary>Q1.1 (#253). Trigger price for StopLoss/StopLimit; null otherwise.</summary>
    decimal? StopPrice = null,
    /// <summary>Q1.1 (#253). Expiry timestamp for GTD; null otherwise.</summary>
    DateTimeOffset? GoodTillDate = null);

public sealed record PositionDto(
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice);

/// <summary>
/// Wire shape for <c>GET /balance</c>. Slice 1 of #107 exposes only
/// <see cref="Available"/>; reserved/total are placeholders for slice 2
/// when the margin provider plugs into the same ledger.
/// </summary>
public sealed record BalanceDto(decimal Available);

public sealed record ExecutionDto(
    string ClOrdId,
    string Symbol,
    string Side,
    string Status,
    string Kind,
    long LeavesQuantity,
    long CumulativeQuantity,
    long LastQuantity,
    decimal LastPrice,
    string? RejectReason,
    DateTimeOffset TimestampUtc,
    bool IsNativeStp = false);

/// <summary>
/// Wire shape for an algo parent. Per-type parameters live in the
/// nullable <see cref="Iceberg"/> / <see cref="Twap"/> properties — only
/// the one matching <see cref="Type"/> is populated. The discriminated
/// shape keeps the JSON debuggable (<c>jq '.iceberg.displayQuantity'</c>)
/// without requiring polymorphic STJ converters on the client.
/// </summary>
public sealed record AlgoDto(
    string AlgoId,
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long TotalQuantity,
    long FilledQuantity,
    long RemainingQuantity,
    string Status,
    string TerminalReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    IcebergParamsDto? Iceberg,
    TwapParamsDto? Twap);

public sealed record IcebergParamsDto(long DisplayQuantity, decimal? LimitPrice);

public sealed record TwapParamsDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int SliceCount,
    string ChildOrderType,
    decimal? ChildPrice);

public static class DtoMappings
{
    public static OrderDto ToDto(this Order o) => new(
        o.ClOrdId.ToString(), o.Symbol, o.SecurityId, o.Side.ToString(), o.Type.ToString(),
        o.Quantity, o.LeavesQuantity, o.CumulativeQuantity, o.Price, o.Status.ToString(),
        o.ParentAlgoId?.ToString(), o.AlgoSliceSeq,
        o.IsStale, o.StaleReason, o.StaledAtUtc,
        o.TimeInForce.ToString(), o.StopPrice, o.GoodTillDate);

    public static PositionDto ToDto(this Position p) => new(p.Symbol, p.NetQuantity, p.AverageEntryPrice);

    public static ExecutionDto ToDto(this ExecutionEvent ev) => new(
        ev.ClOrdId.ToString(), ev.Symbol, ev.Side.ToString(), ev.Status.ToString(), ev.Kind.ToString(),
        ev.LeavesQuantity, ev.CumulativeQuantity, ev.LastQuantity, ev.LastPrice, ev.RejectReason, ev.TimestampUtc,
        ev.IsNativeStp);

    public static AlgoDto ToDto(this Algo a)
    {
        IcebergParamsDto? iceberg = a.Parameters is IcebergParameters ip
            ? new IcebergParamsDto(ip.DisplayQuantity, ip.LimitPrice)
            : null;
        TwapParamsDto? twap = a.Parameters is TwapParameters tp
            ? new TwapParamsDto(tp.StartUtc, tp.EndUtc, tp.SliceCount, tp.ChildOrderType.ToString(), tp.ChildPrice)
            : null;
        return new AlgoDto(
            a.AlgoId.ToString(),
            a.Symbol,
            a.SecurityId,
            a.Side.ToString(),
            a.Type.ToString(),
            a.TotalQuantity,
            a.FilledQuantity,
            a.RemainingQuantity,
            a.Status.ToString(),
            a.TerminalReason.ToString(),
            a.CreatedAtUtc,
            a.TerminalAtUtc,
            iceberg,
            twap);
    }
}
