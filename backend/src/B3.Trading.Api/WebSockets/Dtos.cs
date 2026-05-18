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
    /// <summary>Q2.4 (#271). Realized + unrealized P&amp;L per end-client.</summary>
    public const string PnlMe = "pnl.me";

    /// <summary>
    /// Q1.5 (#257). Public per-symbol market-data channels of the form
    /// <c>phases.${symbol}</c> and <c>auction.${symbol}</c> — fed off
    /// the UMDF auction listener via <c>AuctionStateStore</c>. They are
    /// authenticated (the WS hub still requires a valid bearer) but
    /// not per-firm filtered: any logged-in client may subscribe.
    /// </summary>
    public const string PhasesPrefix = "phases.";
    public const string AuctionPrefix = "auction.";

    /// <summary>
    /// Per-owner channel names (validated against an exact set).
    /// Per-symbol public channels (<see cref="PhasesPrefix"/> /
    /// <see cref="AuctionPrefix"/>) are validated separately via
    /// <see cref="TryParsePublic"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { OrdersMe, ExecutionsMe, PositionsMe, AlgoMe, PnlMe };

    /// <summary>
    /// Recognises <c>phases.SYMBOL</c> / <c>auction.SYMBOL</c> and
    /// returns the <paramref name="kind"/> + <paramref name="symbol"/>.
    /// Symbol validation matches the rest of the trading host:
    /// non-empty, ≤ 16 chars, ASCII alpha-numeric only.
    /// </summary>
    public static bool TryParsePublic(string channel, out PublicChannelKind kind, out string symbol)
    {
        kind = PublicChannelKind.None;
        symbol = string.Empty;
        if (string.IsNullOrEmpty(channel)) return false;

        string raw;
        if (channel.StartsWith(PhasesPrefix, StringComparison.Ordinal))
        {
            kind = PublicChannelKind.Phases;
            raw = channel[PhasesPrefix.Length..];
        }
        else if (channel.StartsWith(AuctionPrefix, StringComparison.Ordinal))
        {
            kind = PublicChannelKind.Auction;
            raw = channel[AuctionPrefix.Length..];
        }
        else
        {
            return false;
        }

        if (raw.Length is 0 or > 16) { kind = PublicChannelKind.None; return false; }
        foreach (var c in raw)
        {
            if (!(char.IsAsciiLetterOrDigit(c)))
            {
                kind = PublicChannelKind.None;
                return false;
            }
        }
        symbol = raw;
        return true;
    }

    public static string PhasesFor(string symbol) => PhasesPrefix + symbol;
    public static string AuctionFor(string symbol) => AuctionPrefix + symbol;
}

public enum PublicChannelKind
{
    None,
    Phases,
    Auction,
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
    DateTimeOffset? GoodTillDate = null,
    /// <summary>Q3.4 (#284). Native iceberg / reserve display quantity; null for full disclosure.</summary>
    long? DisplayQty = null,
    /// <summary>Q3.4 (#284). Refresh policy for the visible portion of an iceberg order;
    /// null iff <see cref="DisplayQty"/> is null. Today only <c>"Always"</c> is accepted
    /// at intake (SDK limitation — see #298).</summary>
    string? DisplayResetPolicy = null,
    /// <summary>Q4.1 (#301). Sub-account bucket this order is booked
    /// against. Null = master bucket (legacy / non-sub-account flow).</summary>
    string? SubAccountId = null);

public sealed record PositionDto(
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice,
    /// <summary>Q4.1 (#301). Sub-account this row belongs to. Null on
    /// the master-aggregate row (sum across all sub-accounts plus the
    /// untagged bucket) returned when the caller did not pass
    /// <c>?subAccount=X</c>.</summary>
    string? SubAccountId = null);

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
    TwapParamsDto? Twap,
    VwapParamsDto? Vwap = null,
    PovParamsDto? Pov = null,
    PeggedParamsDto? Pegged = null);

public sealed record IcebergParamsDto(long DisplayQuantity, decimal? LimitPrice);

public sealed record TwapParamsDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int SliceCount,
    string ChildOrderType,
    decimal? ChildPrice);

/// <summary>
/// Wire shape for VWAP parameters (Q3.1 / #281). Tick interval is
/// surfaced in seconds (the WAL persists ticks; this is a UX choice for
/// the JSON wire) so dashboards and clients don't have to know the
/// .NET tick unit.
/// </summary>
public sealed record VwapParamsDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string ChildOrderType,
    decimal? ChildPrice,
    double TickIntervalSeconds,
    decimal? SliceMaxPct,
    decimal? PriceLimit,
    decimal? ParticipationCap);

/// <summary>
/// Wire shape for POV parameters (Q3.2 / #282). Tick interval surfaced
/// in seconds (the WAL persists .NET ticks; this is a UX choice for the
/// wire) so dashboards / clients don't have to know the .NET tick unit.
/// </summary>
public sealed record PovParamsDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string ChildOrderType,
    decimal? ChildPrice,
    decimal ParticipationRate,
    double TickIntervalSeconds,
    decimal? PriceLimit,
    long MinSliceQty);

/// <summary>
/// Wire shape for Pegged parameters (Q3.3 / #283). RepegInterval is
/// surfaced in milliseconds (sub-second cadence is the common case)
/// while VWAP/POV use seconds — kept separate so each algo's wire
/// matches its operational time scale.
/// </summary>
public sealed record PeggedParamsDto(
    string Ref,
    int OffsetTicks,
    int RepegIntervalMs,
    decimal TickSize,
    string ChildOrderType,
    decimal? PriceLimit);

public static class DtoMappings
{
    public static OrderDto ToDto(this Order o) => new(
        o.ClOrdId.ToString(), o.Symbol, o.SecurityId, o.Side.ToString(), o.Type.ToString(),
        o.Quantity, o.LeavesQuantity, o.CumulativeQuantity, o.Price, o.Status.ToString(),
        o.ParentAlgoId?.ToString(), o.AlgoSliceSeq,
        o.IsStale, o.StaleReason, o.StaledAtUtc,
        o.TimeInForce.ToString(), o.StopPrice, o.GoodTillDate,
        o.DisplayQty, o.DisplayResetPolicy?.ToString(),
        o.SubAccountId?.Value);

    public static PositionDto ToDto(this Position p) => new(p.Symbol, p.NetQuantity, p.AverageEntryPrice);

    public static PositionDto ToDto(this Position p, SubAccountId? subAccount) =>
        new(p.Symbol, p.NetQuantity, p.AverageEntryPrice, subAccount?.Value);

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
        VwapParamsDto? vwap = a.Parameters is VwapParameters vp
            ? new VwapParamsDto(vp.StartUtc, vp.EndUtc, vp.ChildOrderType.ToString(), vp.ChildPrice,
                vp.TickInterval.TotalSeconds, vp.SliceMaxPct, vp.PriceLimit, vp.ParticipationCap)
            : null;
        PovParamsDto? pov = a.Parameters is PovParameters pp
            ? new PovParamsDto(pp.StartUtc, pp.EndUtc, pp.ChildOrderType.ToString(), pp.ChildPrice,
                pp.ParticipationRate, pp.TickInterval.TotalSeconds, pp.PriceLimit, pp.MinSliceQty)
            : null;
        PeggedParamsDto? pegged = a.Parameters is PeggedParameters pgp
            ? new PeggedParamsDto(pgp.Ref.ToString(), pgp.OffsetTicks,
                (int)pgp.RepegInterval.TotalMilliseconds, pgp.TickSize,
                pgp.ChildOrderType.ToString(), pgp.PriceLimit)
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
            twap,
            vwap,
            pov,
            pegged);
    }
}
