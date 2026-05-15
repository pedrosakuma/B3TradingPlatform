using B3.Trading.Application.MarketData;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Q1.5 (#257). WebSocket fan-out for the public per-symbol auction
/// channels (<c>phases.${symbol}</c> and <c>auction.${symbol}</c>).
///
/// <para>
/// Listens to <see cref="AuctionStateStore"/> events and broadcasts
/// deltas to all subscribed clients. Snapshot bootstrap is served
/// through <see cref="IPublicChannelSnapshots"/> (this sink also
/// implements that interface — the registration uses a single
/// instance so the snapshot read and the delta fan-out share the
/// same store). Hosted-service plumbing wires / unwires the event
/// handlers on host start / stop.
/// </para>
///
/// <para>
/// Snapshot wire shape:
/// <list type="bullet">
///   <item><c>phases.${symbol}</c> →
///         <see cref="PhaseSnapshotDto"/> (current
///         <see cref="TradingPhase"/> + the timestamp the store last
///         observed it; <see cref="TradingPhase.Unknown"/> when no
///         frame has been seen yet).</item>
///   <item><c>auction.${symbol}</c> →
///         <see cref="AuctionSnapshotDto"/> (last theoretical top +
///         imbalance — <c>null</c> when no frame seen yet).</item>
/// </list>
/// Deltas reuse the same record types: phases-channel deltas are
/// <see cref="PhaseSnapshotDto"/> (single-phase update); auction-channel
/// deltas are <see cref="AuctionSnapshotDto"/> (top / imbalance update)
/// or <see cref="AuctionPrintDto"/> (cross print).
/// </para>
/// </summary>
public sealed class WebSocketAuctionEventSink : IPublicChannelSnapshots, IHostedService
{
    private readonly SubscriptionManager _subs;
    private readonly AuctionStateStore _store;

    public WebSocketAuctionEventSink(SubscriptionManager subs, AuctionStateStore store)
    {
        _subs = subs;
        _store = store;
    }

    // ---------------- IPublicChannelSnapshots ----------------

    public object? GetSnapshot(PublicChannelKind kind, string symbol) => kind switch
    {
        PublicChannelKind.Phases => new PhaseSnapshotDto(
            symbol,
            _store.GetPhase(symbol).ToString(),
            // No "last phase change" timestamp known when we've never
            // observed a transition — leave At null so clients can
            // distinguish "no signal" from "Unknown was emitted".
            _store.TryGetLastPhaseChange(symbol, out var pc) ? pc!.At : null),
        PublicChannelKind.Auction => _store.TryGetTop(symbol, out var top) && top is not null
            ? AuctionSnapshotDto.From(top)
            : new AuctionSnapshotDto(symbol, null, null, null, null, null, null),
        _ => null,
    };

    // ---------------- IHostedService ----------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.PhaseChanged += OnPhaseChanged;
        _store.TopUpdated += OnTopOrImbalance;
        _store.ImbalanceUpdated += OnTopOrImbalance;
        _store.PrintReceived += OnPrint;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.PhaseChanged -= OnPhaseChanged;
        _store.TopUpdated -= OnTopOrImbalance;
        _store.ImbalanceUpdated -= OnTopOrImbalance;
        _store.PrintReceived -= OnPrint;
        return Task.CompletedTask;
    }

    // ---------------- Event handlers ----------------

    private void OnPhaseChanged(PhaseChange pc) =>
        _subs.BroadcastPublic(
            Channels.PhasesFor(pc.Symbol),
            new PhaseSnapshotDto(pc.Symbol, pc.Phase.ToString(), pc.At));

    private void OnTopOrImbalance(AuctionTopState top) =>
        _subs.BroadcastPublic(
            Channels.AuctionFor(top.Symbol),
            AuctionSnapshotDto.From(top));

    private void OnPrint(AuctionPrint p) =>
        _subs.BroadcastPublic(
            Channels.AuctionFor(p.Symbol),
            new AuctionPrintDto(p.Symbol, p.Kind.ToString(), p.Price, p.Qty, p.At));
}

/// <summary>
/// Wire shape for <c>phases.${symbol}</c>. <see cref="At"/> is null
/// when the store has never observed a phase transition for the
/// symbol (snapshot served as "Unknown" with no timestamp).
/// </summary>
public sealed record PhaseSnapshotDto(string Symbol, string Phase, DateTimeOffset? At);

/// <summary>
/// Wire shape for the top / imbalance state on <c>auction.${symbol}</c>.
/// All numeric fields are nullable so the empty-state snapshot ships a
/// stable shape with <c>null</c>s rather than zeroes that look like
/// real data.
/// </summary>
public sealed record AuctionSnapshotDto(
    string Symbol,
    decimal? Top,
    long? IndicativeMatchQty,
    long? Imbalance,
    string? ImbalanceSide,
    DateTimeOffset? At,
    string? Kind = null) // reserved for future use; null on top updates
{
    public static AuctionSnapshotDto From(AuctionTopState s) =>
        new(s.Symbol, s.Top, s.IndicativeMatchQty, s.Imbalance, s.ImbalanceSide.ToString(), s.At);
}

/// <summary>
/// Wire shape for an auction cross print delta on
/// <c>auction.${symbol}</c>. Distinct from
/// <see cref="AuctionSnapshotDto"/> so subscribers can pattern-match
/// on the discriminator (<c>Kind</c> here is always set; <c>Top</c>
/// is intentionally absent — the print itself is the cross price).
/// </summary>
public sealed record AuctionPrintDto(
    string Symbol,
    string Kind,
    decimal Price,
    long Qty,
    DateTimeOffset At);
