using System.Collections.Concurrent;
using System.Threading.Channels;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// #386. Bridges <see cref="CashLedger.BalanceChanged"/> to the
/// <c>balance.me</c> WS channel. One delta per (firm, owner, Available)
/// change reaches subscribed clients; repeated mutations that leave
/// <c>Available</c> unchanged are coalesced out so a fee that debits
/// 0 (which is a no-op in the ledger anyway) cannot trigger a
/// redundant publish.
///
/// <para>
/// <b>Lock ordering.</b> The keeper raises the event while holding
/// the per-balance lock (so subscribers observe a consistent value).
/// To avoid the WS hub's per-owner publish lock nesting under the
/// ledger lock, the handler only ENQUEUES (firm, owner, available) onto a
/// channel; a dedicated drain task pumps the queue and calls
/// <see cref="SubscriptionManager.Publish(EndClientId, string?, string, object)"/>
/// out from under the keeper lock.
/// </para>
///
/// <para>
/// <b>Firm scope.</b> The concrete firm is carried from the ledger event
/// through the channel and into <see cref="SubscriptionManager.Publish"/>,
/// so a shared end-client identity cannot observe another firm's cash.
/// </para>
/// </summary>
public sealed class WebSocketBalanceFanOut : IHostedService, IAsyncDisposable
{
    private readonly CashLedger _cash;
    private readonly SubscriptionManager _subs;
    private readonly ILogger<WebSocketBalanceFanOut>? _logger;

    private readonly ConcurrentDictionary<(string FirmId, EndClientId Owner), decimal> _lastSent = new();

    private readonly Channel<(string FirmId, EndClientId Owner, decimal Available)> _channel =
        Channel.CreateUnbounded<(string, EndClientId, decimal)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private Task? _drainTask;
    private int _stopped;

    public WebSocketBalanceFanOut(
        CashLedger cash,
        SubscriptionManager subs,
        ILogger<WebSocketBalanceFanOut>? logger = null)
    {
        _cash = cash;
        _subs = subs;
        _logger = logger;
        _cash.BalanceChanged += OnBalanceChanged;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _drainTask = Task.Run(DrainAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cash.BalanceChanged -= OnBalanceChanged;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        if (_drainTask is not null)
        {
            try { await _drainTask.ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }

    private void OnBalanceChanged(string firmId, EndClientId owner, decimal available)
    {
        // Enqueue under the keeper lock; the drain runs outside it.
        _channel.Writer.TryWrite((firmId, owner, available));
    }

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    try { PublishIfChanged(item.FirmId, item.Owner, item.Available); }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "balance.me fan-out failed for firm={Firm} owner={Owner}",
                            item.FirmId, item.Owner.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void PublishIfChanged(string firmId, EndClientId owner, decimal available)
    {
        var key = (firmId, owner);
        // Coalesce: skip when the most recently published Available for
        // this firm/owner is identical. Decimals compare by value (not scale),
        // which is what we want — 0 == 0.00.
        if (_lastSent.TryGetValue(key, out var prev) && prev == available)
            return;

        if (_subs.CountFor(owner) == 0)
        {
            // No subscribers — still update _lastSent so the first publish
            // after a subscriber attaches reflects the snapshot baseline.
            _lastSent[key] = available;
            return;
        }

        _subs.Publish(owner, firmId, Channels.BalanceMe, new BalanceDto(available));
        _lastSent[key] = available;
    }
}
