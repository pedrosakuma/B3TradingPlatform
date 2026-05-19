using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Q3.6 Stage B (#286). WebSocket fan-out for the public per-symbol
/// top-of-book channel (<c>book.${symbol}</c>). Listens to
/// <see cref="MboBookStore.TopChanged"/> and broadcasts deltas to all
/// subscribed clients; snapshot bootstrap is served through
/// <see cref="IPublicChannelSnapshots"/> so a newly-subscribed client
/// always sees the current top before the next delta lands.
///
/// <para>
/// Coalesces no-op updates: an apply that did not change the derived
/// top-of-book (e.g. a deep-book add that did not affect the best
/// price/qty/order-count) is dropped so quiet symbols stay quiet on
/// the wire. The "last sent" snapshot is kept per symbol; the first
/// observed update for a symbol is always emitted.
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
    private readonly MboBookStore _store;

    // Last DTO we put on the wire for each symbol — used to coalesce
    // identical updates (no-op deep-book mutations). Keyed
    // case-insensitively to match the store.
    private readonly ConcurrentDictionary<string, L2TopOfBookDto> _lastSent =
        new(StringComparer.OrdinalIgnoreCase);

    public WebSocketBookEventSink(SubscriptionManager subs, MboBookStore store)
    {
        _subs = subs;
        _store = store;
    }

    // ---------------- IPublicChannelSnapshots ----------------

    public object? GetSnapshot(PublicChannelKind kind, string symbol) => kind switch
    {
        PublicChannelKind.Book => _store.GetTopOfBook(symbol) is { } top
            ? L2TopOfBookDto.From(top)
            : L2TopOfBookDto.Empty(symbol),
        _ => null,
    };

    // ---------------- IHostedService ----------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.TopChanged += OnTopChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.TopChanged -= OnTopChanged;
        return Task.CompletedTask;
    }

    // ---------------- Event handlers ----------------

    private void OnTopChanged(L2TopOfBook? top)
    {
        if (top is null) return; // nothing to broadcast when both sides went empty
        var dto = L2TopOfBookDto.From(top.Value);
        // Coalesce: skip if best bid+ask are identical to last sent for
        // this symbol — UpdatedUtc is intentionally excluded so a
        // deep-book mutation that bumped the store's timestamp but did
        // not move the top stays off the wire.
        if (_lastSent.TryGetValue(dto.Symbol, out var prev) &&
            Equals(prev.Bid, dto.Bid) && Equals(prev.Ask, dto.Ask))
            return;
        _lastSent[dto.Symbol] = dto;
        _subs.BroadcastPublic(Channels.BookFor(dto.Symbol), dto);
    }
}
