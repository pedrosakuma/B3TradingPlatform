using System.Collections.Concurrent;
using System.Threading.Channels;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// #386. Bridges <see cref="CashLedger.BalanceChanged"/> to the
/// <c>balance.me</c> WS channel. One delta per (owner, Available)
/// change reaches subscribed clients; repeated mutations that leave
/// <c>Available</c> unchanged are coalesced out so a fee that debits
/// 0 (which is a no-op in the ledger anyway) cannot trigger a
/// redundant publish.
///
/// <para>
/// <b>Lock ordering.</b> The keeper raises the event while holding
/// the per-balance lock (so subscribers observe a consistent value).
/// To avoid the WS hub's per-owner publish lock nesting under the
/// ledger lock, the handler only ENQUEUES (owner, available) onto a
/// channel; a dedicated drain task pumps the queue and calls
/// <see cref="SubscriptionManager.Publish(EndClientId, string?, string, object)"/>
/// out from under the keeper lock.
/// </para>
///
/// <para>
/// <b>Firm scope.</b> <see cref="CashLedger"/> is keyed only by
/// <see cref="EndClientId"/>; the balance for a given owner is the
/// same regardless of which firm authenticated the WS session. The
/// publish therefore passes <c>firmId: null</c> and fans out to every
/// subscribed client of the owner.
/// </para>
/// </summary>
public sealed class WebSocketBalanceFanOut : IHostedService, IAsyncDisposable
{
    private readonly CashLedger _cash;
    private readonly SubscriptionManager _subs;
    private readonly ILogger<WebSocketBalanceFanOut>? _logger;

    private readonly ConcurrentDictionary<EndClientId, decimal> _lastSent = new();

    private readonly Channel<(EndClientId Owner, decimal Available)> _channel =
        Channel.CreateUnbounded<(EndClientId, decimal)>(
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

    private void OnBalanceChanged(EndClientId owner, decimal available)
    {
        // Enqueue under the keeper lock; the drain runs outside it.
        _channel.Writer.TryWrite((owner, available));
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
                    try { PublishIfChanged(item.Owner, item.Available); }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "balance.me fan-out failed for owner={Owner}", item.Owner.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void PublishIfChanged(EndClientId owner, decimal available)
    {
        // Coalesce: skip when the most recently published Available for
        // this owner is identical. Decimals compare by value (not scale),
        // which is what we want — 0 == 0.00.
        if (_lastSent.TryGetValue(owner, out var prev) && prev == available)
            return;

        if (_subs.CountFor(owner) == 0)
        {
            // No subscribers — still update _lastSent so the first publish
            // after a subscriber attaches reflects the snapshot baseline.
            _lastSent[owner] = available;
            return;
        }

        _subs.Publish(owner, firmId: null, Channels.BalanceMe, new BalanceDto(available));
        _lastSent[owner] = available;
    }
}
