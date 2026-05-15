using System.Collections.Concurrent;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Per-symbol cache of auction "top" + imbalance + current trading
/// phase, fed from the UMDF market-data listener
/// (<see cref="IMarketDataSubscriber"/>) and consumed by:
/// <list type="bullet">
///   <item><see cref="IPhaseProvider"/> — the risk pipeline (#254)
///         consults this on every order entry to enforce phase-aware
///         rules (GFA / auction-only / etc.).</item>
///   <item>The WebSocket fan-out for <c>phases.${symbol}</c> /
///         <c>auction.${symbol}</c> public channels.</item>
/// </list>
///
/// <para><b>Concurrency.</b> Per-symbol mutable record under a
/// per-symbol <c>lock</c> (matches <see cref="PositionKeeper"/>'s
/// pattern). The dictionary itself is a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; reads
/// (<see cref="GetPhase"/>, <see cref="TryGetTop"/>) take the entry
/// lock to read a coherent tuple.</para>
///
/// <para><b>Phase derivation heuristic.</b> Matching does not (today)
/// emit a dedicated <c>PhaseChange</c> frame, so phase is implied:
/// <list type="bullet">
///   <item><c>TheoreticalOpening</c> received → phase becomes
///         <see cref="TradingPhase.OpeningCall"/> (idempotent).</item>
///   <item><c>AuctionPrint(Opening)</c> → <see cref="TradingPhase.Open"/>.</item>
///   <item><c>AuctionPrint(Closing)</c> → <see cref="TradingPhase.Close"/>.</item>
///   <item>No frame seen yet → <see cref="TradingPhase.Unknown"/>.</item>
/// </list>
/// Continuous trading without prior auction signal stays
/// <see cref="TradingPhase.Unknown"/>; risk treats Unknown as
/// "no auction overlay applies". The full session scheduler
/// (<see cref="TradingPhase.Reserved"/>, <see cref="TradingPhase.FinalClosingCall"/>)
/// is upstream's responsibility — see
/// <c>B3MatchingPlatform#321 / #322</c>.</para>
/// </summary>
public sealed class AuctionStateStore : IPhaseProvider, IHostedService
{
    private readonly IMarketDataSubscriber _subscriber;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuctionStateStore>? _logger;

    private readonly ConcurrentDictionary<string, SymbolEntry> _bySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a top / indicative-qty update.</summary>
    public event Action<AuctionTopState>? TopUpdated;

    /// <summary>Raised after an imbalance update (folded into the same top state).</summary>
    public event Action<AuctionTopState>? ImbalanceUpdated;

    /// <summary>Raised when an auction cross prints.</summary>
    public event Action<AuctionPrint>? PrintReceived;

    /// <summary>Raised when a symbol's phase actually moves (idempotent suppressed).</summary>
    public event Action<PhaseChange>? PhaseChanged;

    public AuctionStateStore(
        IMarketDataSubscriber subscriber,
        TimeProvider? clock = null,
        ILogger<AuctionStateStore>? logger = null)
    {
        _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
        _clock = clock ?? TimeProvider.System;
        _logger = logger;

        _subscriber.TheoreticalOpening += OnTheoreticalOpening;
        _subscriber.AuctionImbalance += OnAuctionImbalance;
        _subscriber.AuctionPrint += OnAuctionPrint;
    }

    // ---------------- IPhaseProvider ----------------

    public TradingPhase GetPhase(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return TradingPhase.Unknown;
        if (!_bySymbol.TryGetValue(symbol.Trim(), out var entry))
            return TradingPhase.Unknown;
        lock (entry.Sync)
            return entry.Phase;
    }

    // ---------------- Public reads ----------------

    /// <summary>
    /// Returns the most recent <see cref="AuctionTopState"/> for
    /// <paramref name="symbol"/> if one has ever been observed.
    /// </summary>
    public bool TryGetTop(string symbol, out AuctionTopState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(symbol))
            return false;
        var key = symbol.Trim();
        if (!_bySymbol.TryGetValue(key, out var entry))
            return false;
        lock (entry.Sync)
        {
            if (!entry.HasTop)
                return false;
            state = entry.ToTopStateUnderLock(key);
            return true;
        }
    }

    /// <summary>
    /// Read-only snapshot of every symbol the store has observed.
    /// O(N) — intended for ops introspection / WS snapshot bootstrap.
    /// </summary>
    public IReadOnlyDictionary<string, AuctionTopState> SnapshotTops()
    {
        var dict = new Dictionary<string, AuctionTopState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, entry) in _bySymbol)
        {
            lock (entry.Sync)
            {
                if (entry.HasTop)
                    dict[symbol] = entry.ToTopStateUnderLock(symbol);
            }
        }
        return dict;
    }

    /// <summary>
    /// Read-only snapshot of every symbol's current phase. Symbols
    /// never observed are absent (callers fall back to
    /// <see cref="TradingPhase.Unknown"/>).
    /// </summary>
    public IReadOnlyDictionary<string, PhaseChange> SnapshotPhases()
    {
        var dict = new Dictionary<string, PhaseChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, entry) in _bySymbol)
        {
            lock (entry.Sync)
            {
                if (entry.Phase != TradingPhase.Unknown)
                    dict[symbol] = new PhaseChange(symbol, entry.Phase, entry.PhaseAt);
            }
        }
        return dict;
    }

    /// <summary>
    /// Returns the last phase transition for <paramref name="symbol"/>
    /// — or <c>false</c> when no transition has ever been observed.
    /// </summary>
    public bool TryGetLastPhaseChange(string symbol, out PhaseChange? change)
    {
        change = null;
        if (string.IsNullOrWhiteSpace(symbol))
            return false;
        var key = symbol.Trim();
        if (!_bySymbol.TryGetValue(key, out var entry))
            return false;
        lock (entry.Sync)
        {
            if (entry.Phase == TradingPhase.Unknown)
                return false;
            change = new PhaseChange(key, entry.Phase, entry.PhaseAt);
            return true;
        }
    }

    // ---------------- Listener handlers ----------------

    internal void OnTheoreticalOpening(MarketTheoreticalOpening ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Symbol)) return;

        var symbol = ev.Symbol.Trim();
        var entry = _bySymbol.GetOrAdd(symbol, _ => new SymbolEntry());

        AuctionTopState topAfter;
        PhaseChange? phaseDelta;
        lock (entry.Sync)
        {
            entry.HasTop = true;
            entry.Top = ev.Price;
            entry.IndicativeMatchQty = ev.Qty;
            entry.LastUpdateAt = ev.ReceivedUtc;
            phaseDelta = entry.TransitionTo(TradingPhase.OpeningCall, ev.ReceivedUtc, symbol);
            topAfter = entry.ToTopStateUnderLock(symbol);
        }

        TopUpdated?.Invoke(topAfter);
        if (phaseDelta is not null) PhaseChanged?.Invoke(phaseDelta);
    }

    internal void OnAuctionImbalance(MarketAuctionImbalance ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Symbol)) return;

        var symbol = ev.Symbol.Trim();
        var entry = _bySymbol.GetOrAdd(symbol, _ => new SymbolEntry());

        AuctionTopState topAfter;
        lock (entry.Sync)
        {
            entry.HasTop = true;
            entry.Imbalance = ev.Quantity;
            entry.ImbalanceSide = ev.Side;
            entry.LastUpdateAt = ev.ReceivedUtc;
            topAfter = entry.ToTopStateUnderLock(symbol);
        }

        ImbalanceUpdated?.Invoke(topAfter);
    }

    internal void OnAuctionPrint(MarketAuctionPrint ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Symbol)) return;

        var symbol = ev.Symbol.Trim();
        var entry = _bySymbol.GetOrAdd(symbol, _ => new SymbolEntry());

        PhaseChange? phaseDelta;
        lock (entry.Sync)
        {
            // Auction prints clear the indicative top — the cross has
            // happened, the pre-cross theoretical top is now stale.
            // Reset retained top / imbalance under the lock so
            // TryGetTop / SnapshotTops stop returning the dead state;
            // the next top is whatever the next TheoreticalOpening
            // reports (e.g. a re-auction) or nothing at all.
            entry.HasTop = false;
            entry.Top = default;
            entry.IndicativeMatchQty = default;
            entry.Imbalance = default;
            entry.ImbalanceSide = default;
            entry.LastUpdateAt = ev.ReceivedUtc;
            var nextPhase = ev.Kind == AuctionPrintKind.Opening
                ? TradingPhase.Open
                : TradingPhase.Close;
            phaseDelta = entry.TransitionTo(nextPhase, ev.ReceivedUtc, symbol);
        }

        PrintReceived?.Invoke(new AuctionPrint(symbol, ev.Kind, ev.Price, ev.Qty, ev.ReceivedUtc));
        if (phaseDelta is not null) PhaseChanged?.Invoke(phaseDelta);
    }

    // ---------------- Hosted-service plumbing ----------------

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriber.TheoreticalOpening -= OnTheoreticalOpening;
        _subscriber.AuctionImbalance -= OnAuctionImbalance;
        _subscriber.AuctionPrint -= OnAuctionPrint;
        return Task.CompletedTask;
    }

    // ---------------- Internals ----------------

    private sealed class SymbolEntry
    {
        public readonly object Sync = new();
        public bool HasTop;
        public decimal Top;
        public long IndicativeMatchQty;
        public long Imbalance;
        public OrderSide ImbalanceSide;
        public DateTimeOffset LastUpdateAt;
        public TradingPhase Phase = TradingPhase.Unknown;
        public DateTimeOffset PhaseAt;

        public PhaseChange? TransitionTo(TradingPhase next, DateTimeOffset at, string symbol)
        {
            if (Phase == next)
                return null;
            Phase = next;
            PhaseAt = at;
            return new PhaseChange(symbol, next, at);
        }

        public AuctionTopState ToTopStateUnderLock(string symbol) =>
            new(symbol, Top, IndicativeMatchQty, Imbalance, ImbalanceSide, LastUpdateAt);
    }
}
