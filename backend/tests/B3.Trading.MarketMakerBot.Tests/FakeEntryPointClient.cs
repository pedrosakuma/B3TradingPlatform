using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.Risk;
using B3.EntryPoint.Client.Telemetry;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace B3.Trading.MarketMakerBot.Tests;

/// <summary>
/// Minimal <see cref="IEntryPointClient"/> test double — the seam
/// requested in pedrosakuma/B3EntryPointClient#227 and adopted here per
/// #709, so <c>MarketMakerWorker</c>'s event-handling logic (the actual
/// source of the #707 duplicate-order bug) can be driven deterministically
/// instead of only via a live Docker soak test. Only the members
/// <c>MarketMakerWorker</c> actually calls do anything meaningful;
/// everything else throws <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class FakeEntryPointClient : IEntryPointClient
{
    private EventHandler<TerminatedEventArgs>? _terminated;
    public List<NewOrderRequest> SubmittedOrders { get; } = new();
    public List<CancelOrderRequest> SubmittedCancels { get; } = new();
    public List<MassActionRequest> SubmittedMassActions { get; } = new();
    public ConcurrentQueue<string> Operations { get; } = new();
    public Func<NewOrderRequest, CancellationToken, Task>? SubmitHandler { get; set; }
    public Func<CancelOrderRequest, CancellationToken, Task>? CancelHandler { get; set; }
    public Func<MassActionRequest, CancellationToken, Task<MassActionReport>>? MassActionHandler { get; set; }
    public Func<CancellationToken, Task>? ConnectHandler { get; set; }
    private readonly Channel<EntryPointEvent> _events = Channel.CreateUnbounded<EntryPointEvent>();

    public async Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct)
    {
        Operations.Enqueue("submit");
        SubmittedOrders.Add(request);
        if (SubmitHandler is not null)
            await SubmitHandler(request, ct);
        return request.ClOrdID;
    }

    public async Task CancelAsync(CancelOrderRequest request, CancellationToken ct)
    {
        Operations.Enqueue("cancel");
        SubmittedCancels.Add(request);
        if (CancelHandler is not null)
            await CancelHandler(request, ct);
    }

    // --- Unused by MarketMakerWorker; not exercised by these tests. ---
    public Task<OutboundAttemptReceipt> SubmitWithReceiptAsync(NewOrderRequest request, OutboundFramePreparedCallback callback, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<ClOrdID> SubmitSimpleAsync(SimpleNewOrderRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<OutboundAttemptReceipt> ReplaceWithReceiptAsync(ReplaceOrderRequest request, OutboundFramePreparedCallback callback, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<OutboundAttemptReceipt> CancelWithReceiptAsync(CancelOrderRequest request, OutboundFramePreparedCallback callback, CancellationToken ct) =>
        throw new NotSupportedException();
    public async Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct)
    {
        Operations.Enqueue("mass-action");
        SubmittedMassActions.Add(request);
        if (MassActionHandler is null)
            throw new NotSupportedException();
        return await MassActionHandler(request, ct);
    }
    public Task<string> SubmitCrossAsync(NewOrderCrossRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task SendQuoteRequestAsync(QuoteRequestMessage request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task SendQuoteAsync(QuoteMessage request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task CancelQuoteAsync(string quoteId, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task ConnectAsync(CancellationToken ct) =>
        ConnectHandler?.Invoke(ct) ?? throw new NotSupportedException();
    public Task TerminateAsync(TerminationCode code, CancellationToken ct) => throw new NotSupportedException();
    public Task FlushAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task ReconnectAsync(uint sessionVerId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ReconnectOutcome> ReconnectAsync(ReconnectMode mode, Func<uint, uint>? nextSessionVerIdSelector, CancellationToken ct) =>
        throw new NotSupportedException();
    public ClientHealth GetHealth() => throw new NotSupportedException();
    public async IAsyncEnumerable<EntryPointEvent> Events(
        [EnumeratorCancellation] CancellationToken ct)
    {
        Operations.Enqueue("events-started");
        while (await _events.Reader.WaitToReadAsync(ct))
        {
            while (_events.Reader.TryRead(out var ev))
            {
                Operations.Enqueue($"event:{ev.GetType().Name}");
                yield return ev;
            }
        }
    }
    public FixpClientState State => throw new NotSupportedException();
    public IList<IPreTradeGate> RiskGates => throw new NotSupportedException();
    public IKeepAliveScheduler KeepAlive => throw new NotSupportedException();
    public IRetransmitRequestHandler Retransmit => throw new NotSupportedException();
    public event EventHandler<TerminatedEventArgs>? Terminated
    {
        add => _terminated += value;
        remove => _terminated -= value;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Publish(EntryPointEvent ev) => _events.Writer.TryWrite(ev);
    public void CompleteEvents() => _events.Writer.TryComplete();
    public void PublishTerminated(
        TerminationCode code = TerminationCode.Unspecified,
        string? reason = "transport closed",
        bool initiatedByClient = false) =>
        _terminated?.Invoke(this, new TerminatedEventArgs(code, reason, initiatedByClient));
}
