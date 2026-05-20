using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// #372 / #293. WebSocket fan-out for the public per-symbol L3 (MBO)
/// depth channel (<c>bookmbo.${symbol}</c>). Listens to the raw MBO
/// events on <see cref="IMboBookEventSource"/> (snapshot / order
/// add / update / delete / book cleared) and broadcasts one
/// <see cref="MboBookDeltaDto"/> per event to subscribed clients.
/// Doubles as the snapshot provider for <see cref="PublicChannelKind.BookMbo"/>:
/// new subscribers receive a <see cref="MboBookSnapshotDto"/> of
/// the current per-symbol L3 state before the next delta lands.
///
/// <para>
/// State model: per <c>Symbol</c> we keep a pair of dictionaries
/// (bids / asks) keyed by <c>OrderId</c>. The dictionary is rebuilt
/// from each <c>BookSnapshot</c>, mutated by add/update/delete, and
/// emptied (per side or both) by <c>BookCleared</c>. Snapshot reads
/// take the per-symbol lock; the same lock guards mutation so an
/// in-flight subscribe never sees a torn state.
/// </para>
///
/// <para>
/// No coalescing: an L3 channel exists precisely so consumers see
/// every per-order event. Back-pressure is handled by the existing
/// <see cref="SubscriptionManager"/> bounded channel (drop-with-counter
/// on slow socket).
/// </para>
///
/// <para>
/// When the live feed is off (no <c>WsUrl</c> or
/// <c>MarketDataOptions.EnableBook=false</c>) the registered
/// <see cref="IMboBookEventSource"/> is the no-op implementation;
/// this sink stays idle and snapshots return the empty shape — same
/// posture as <see cref="WebSocketBookEventSink"/> in that mode.
/// </para>
/// </summary>
public sealed class WebSocketMboBookEventSink : IPublicChannelSnapshots, IHostedService
{
    private readonly SubscriptionManager _subs;
    private readonly IMboBookEventSource _source;
    private readonly bool _enabled;

    // Per-symbol L3 state. Each PerSymbol entry is independently
    // locked: snapshot reads + mutations on the same symbol serialize
    // through it; cross-symbol events run lock-free.
    private readonly ConcurrentDictionary<string, PerSymbol> _state =
        new(StringComparer.OrdinalIgnoreCase);

    public WebSocketMboBookEventSink(
        SubscriptionManager subs,
        IMboBookEventSource source,
        IOptions<MarketDataOptions> options)
    {
        _subs = subs;
        _source = source;
        _enabled = options.Value.EnableBook;
    }

    // ---------------- IPublicChannelSnapshots ----------------

    public object? GetSnapshot(PublicChannelKind kind, string symbol)
    {
        if (kind != PublicChannelKind.BookMbo) return null;
        if (string.IsNullOrEmpty(symbol)) return MboBookSnapshotDto.Empty(symbol);
        if (!_state.TryGetValue(symbol, out var per))
            return MboBookSnapshotDto.Empty(symbol);
        return per.Snapshot();
    }

    // ---------------- IHostedService ----------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Even when EnableBook is false we attach handlers — the
        // registered NullMboBookEventSource never raises them, so this
        // costs nothing and keeps the wire-up symmetric with the L2
        // sink which is also unconditional.
        _source.BookSnapshot += OnBookSnapshot;
        _source.OrderAdded += OnOrderAdded;
        _source.OrderUpdated += OnOrderUpdated;
        _source.OrderDeleted += OnOrderDeleted;
        _source.BookCleared += OnBookCleared;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _source.BookSnapshot -= OnBookSnapshot;
        _source.OrderAdded -= OnOrderAdded;
        _source.OrderUpdated -= OnOrderUpdated;
        _source.OrderDeleted -= OnOrderDeleted;
        _source.BookCleared -= OnBookCleared;
        return Task.CompletedTask;
    }

    // ---------------- Event handlers ----------------

    private void OnBookSnapshot(MarketBookSnapshot ev)
    {
        if (!_enabled) return;
        var per = _state.GetOrAdd(ev.Symbol, _ => new PerSymbol(ev.Symbol));
        per.ApplySnapshot(ev);
        // Snapshots are also surfaced as a snapshot frame on the wire
        // — but only late joiners use them; existing subscribers
        // already had per-order deltas. We don't broadcast the
        // snapshot as a delta to avoid double-counting state.
    }

    private void OnOrderAdded(MarketOrderAdded ev)
    {
        if (!_enabled) return;
        var per = _state.GetOrAdd(ev.Symbol, _ => new PerSymbol(ev.Symbol));
        per.ApplyAdded(ev);
        _subs.BroadcastPublic(
            Channels.BookMboFor(ev.Symbol),
            new MboBookDeltaDto("added", ev.Symbol, ev.OrderId.ToString(),
                SideToWire(ev.Side), ev.Price, ev.Qty, ev.ReceivedUtc));
    }

    private void OnOrderUpdated(MarketOrderUpdated ev)
    {
        if (!_enabled) return;
        var per = _state.GetOrAdd(ev.Symbol, _ => new PerSymbol(ev.Symbol));
        per.ApplyUpdated(ev);
        _subs.BroadcastPublic(
            Channels.BookMboFor(ev.Symbol),
            new MboBookDeltaDto("updated", ev.Symbol, ev.OrderId.ToString(),
                SideToWire(ev.Side), ev.Price, ev.Qty, ev.ReceivedUtc));
    }

    private void OnOrderDeleted(MarketOrderDeleted ev)
    {
        if (!_enabled) return;
        if (_state.TryGetValue(ev.Symbol, out var per))
            per.ApplyDeleted(ev);
        _subs.BroadcastPublic(
            Channels.BookMboFor(ev.Symbol),
            new MboBookDeltaDto("deleted", ev.Symbol, ev.OrderId.ToString(),
                SideToWire(ev.Side), null, null, ev.ReceivedUtc));
    }

    private void OnBookCleared(MarketBookCleared ev)
    {
        if (!_enabled) return;
        if (_state.TryGetValue(ev.Symbol, out var per))
            per.ApplyCleared(ev);
        _subs.BroadcastPublic(
            Channels.BookMboFor(ev.Symbol),
            new MboBookDeltaDto("cleared", ev.Symbol, null,
                ClearSideToWire(ev.ClearSide), null, null, ev.ReceivedUtc));
    }

    private static string SideToWire(MarketBookSide s) => s switch
    {
        MarketBookSide.Bid => "bid",
        MarketBookSide.Ask => "ask",
        _ => "bid",
    };

    // null = both sides cleared (matches the SDK semantics).
    private static string? ClearSideToWire(MarketBookClearSide s) => s switch
    {
        MarketBookClearSide.Bid => "bid",
        MarketBookClearSide.Ask => "ask",
        MarketBookClearSide.Both => null,
        _ => null,
    };

    // ---------------- Per-symbol L3 state ----------------

    private sealed class PerSymbol
    {
        private readonly string _symbol;
        private readonly object _lock = new();
        private readonly SortedDictionary<ulong, MarketBookOrder> _bids = new();
        private readonly SortedDictionary<ulong, MarketBookOrder> _asks = new();
        private long? _seq;
        private DateTimeOffset? _updatedUtc;

        public PerSymbol(string symbol) => _symbol = symbol;

        public void ApplySnapshot(MarketBookSnapshot ev)
        {
            lock (_lock)
            {
                _bids.Clear();
                _asks.Clear();
                foreach (var o in ev.Bids) _bids[o.OrderId] = o;
                foreach (var o in ev.Asks) _asks[o.OrderId] = o;
                _seq = ev.RptSeq;
                _updatedUtc = ev.ReceivedUtc;
            }
        }

        public void ApplyAdded(MarketOrderAdded ev)
        {
            lock (_lock)
            {
                var dict = ev.Side == MarketBookSide.Bid ? _bids : _asks;
                dict[ev.OrderId] = new MarketBookOrder(ev.OrderId, ev.Price, ev.Qty);
                _updatedUtc = ev.ReceivedUtc;
            }
        }

        public void ApplyUpdated(MarketOrderUpdated ev)
        {
            lock (_lock)
            {
                var dict = ev.Side == MarketBookSide.Bid ? _bids : _asks;
                dict[ev.OrderId] = new MarketBookOrder(ev.OrderId, ev.Price, ev.Qty);
                _updatedUtc = ev.ReceivedUtc;
            }
        }

        public void ApplyDeleted(MarketOrderDeleted ev)
        {
            lock (_lock)
            {
                var dict = ev.Side == MarketBookSide.Bid ? _bids : _asks;
                dict.Remove(ev.OrderId);
                _updatedUtc = ev.ReceivedUtc;
            }
        }

        public void ApplyCleared(MarketBookCleared ev)
        {
            lock (_lock)
            {
                switch (ev.ClearSide)
                {
                    case MarketBookClearSide.Bid: _bids.Clear(); break;
                    case MarketBookClearSide.Ask: _asks.Clear(); break;
                    case MarketBookClearSide.Both:
                    default:
                        _bids.Clear();
                        _asks.Clear();
                        break;
                }
                _updatedUtc = ev.ReceivedUtc;
            }
        }

        public MboBookSnapshotDto Snapshot()
        {
            lock (_lock)
            {
                return new MboBookSnapshotDto(
                    _symbol,
                    _seq,
                    Project(_bids, bids: true),
                    Project(_asks, bids: false),
                    _updatedUtc);
            }
        }

        // Best-first ordering: bids descending price, asks ascending.
        // Ties broken by OrderId ascending (deterministic + matches the
        // SDK's snapshot ordering).
        private static IReadOnlyList<MboOrderDto> Project(
            SortedDictionary<ulong, MarketBookOrder> src, bool bids)
        {
            if (src.Count == 0) return Array.Empty<MboOrderDto>();
            var arr = new MboOrderDto[src.Count];
            var i = 0;
            foreach (var o in src.Values)
                arr[i++] = new MboOrderDto(o.OrderId.ToString(), o.Price, o.Qty);
            Array.Sort(arr, (a, b) =>
            {
                var cmp = bids ? b.Price.CompareTo(a.Price) : a.Price.CompareTo(b.Price);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.OrderId, b.OrderId);
            });
            return arr;
        }
    }
}
