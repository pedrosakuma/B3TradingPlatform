using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Q3.6 Stage B (#286). WebSocket fan-out for the public per-symbol
/// depth ladder channel (<c>book.${symbol}</c>). Listens to
/// <see cref="IL2BookView.BookChanged"/>; on each event pulls the
/// top-N ladder from the view at the configured depth
/// (<c>MarketDataOptions.BookChannelMaxLevels</c>) and broadcasts a
/// <see cref="L2LadderDto"/> delta to all subscribed clients.
/// Snapshot bootstrap is served through
/// <see cref="IPublicChannelSnapshots"/> so a newly-subscribed client
/// always sees the current ladder before the next delta lands.
///
/// <para>
/// Coalesces no-op updates: an apply that did not change the derived
/// top-N ladder (e.g. a level beyond <c>BookChannelMaxLevels</c>
/// changed, or a deeper-than-top update that left the visible window
/// untouched) is dropped so quiet symbols stay quiet on the wire.
/// The last-sent DTO is kept per symbol; the first observed update
/// for a symbol is always emitted.
/// </para>
///
/// <para>
/// Same auth posture as the other public per-symbol channels
/// (authenticated bearer required; no per-firm filter). When
/// <c>MarketDataOptions.EnableBook=false</c> the underlying store
/// stays empty so this sink is a no-op (snapshots return the empty
/// shape; no deltas fire). Registration is unconditional to keep DI
/// composition simple — the cost is one event handler and an empty
/// dictionary.
/// </para>
/// </summary>
public sealed class WebSocketBookEventSink : IPublicChannelSnapshots, IHostedService
{
    private readonly SubscriptionManager _subs;
    private readonly IL2BookView _store;
    private readonly int _maxLevels;

    // Last DTO put on the wire per symbol — used to coalesce identical
    // updates. Keyed case-insensitively to match the store.
    private readonly ConcurrentDictionary<string, L2LadderDto> _lastSent =
        new(StringComparer.OrdinalIgnoreCase);

    public WebSocketBookEventSink(
        SubscriptionManager subs,
        IL2BookView store,
        IOptions<MarketDataOptions> options)
    {
        _subs = subs;
        _store = store;
        var max = options.Value.BookChannelMaxLevels;
        _maxLevels = max > 0 ? max : 10;
    }

    // ---------------- IPublicChannelSnapshots ----------------

    public object? GetSnapshot(PublicChannelKind kind, string symbol) => kind switch
    {
        PublicChannelKind.Book => _store.GetLadder(symbol, _maxLevels) is { } ladder
            ? L2LadderDto.From(ladder)
            : L2LadderDto.Empty(symbol),
        _ => null,
    };

    // ---------------- IHostedService ----------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.BookChanged += OnBookChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.BookChanged -= OnBookChanged;
        return Task.CompletedTask;
    }

    // ---------------- Event handlers ----------------

    private void OnBookChanged(string symbol)
    {
        var ladder = _store.GetLadder(symbol, _maxLevels);
        if (ladder is null) return; // nothing to broadcast when both sides went empty
        var dto = L2LadderDto.From(ladder.Value);
        // Coalesce: skip if bid/ask ladders are identical to last sent
        // for this symbol — UpdatedUtc is intentionally excluded so a
        // deep-book mutation that bumped the store's timestamp but
        // did not move the visible window stays off the wire.
        if (_lastSent.TryGetValue(dto.Symbol, out var prev) && SidesEqual(prev, dto))
            return;
        _lastSent[dto.Symbol] = dto;
        _subs.BroadcastPublic(Channels.BookFor(dto.Symbol), dto);
    }

    private static bool SidesEqual(L2LadderDto a, L2LadderDto b) =>
        SideEqual(a.Bids, b.Bids) && SideEqual(a.Asks, b.Asks);

    private static bool SideEqual(IReadOnlyList<L2SideDto> a, IReadOnlyList<L2SideDto> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }
}
