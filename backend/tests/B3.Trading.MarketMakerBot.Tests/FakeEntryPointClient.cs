using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.Risk;
using B3.EntryPoint.Client.Telemetry;

namespace B3.Trading.MarketMakerBot.Tests;

/// <summary>
/// Minimal <see cref="IEntryPointClient"/> test double — the seam
/// requested in pedrosakuma/B3EntryPointClient#227 and adopted here per
/// #709, so <c>MarketMakerWorker</c>'s event-handling logic (the actual
/// source of the #707 duplicate-order bug) can be driven deterministically
/// instead of only via a live Docker soak test. Only the members
/// <c>MarketMakerWorker</c> actually calls (<see cref="SubmitAsync"/>,
/// <see cref="CancelAsync"/>) do anything meaningful; everything else
/// throws <see cref="NotSupportedException"/> since the worker never
/// invokes it.
/// </summary>
internal sealed class FakeEntryPointClient : IEntryPointClient
{
    public List<NewOrderRequest> SubmittedOrders { get; } = new();
    public List<CancelOrderRequest> SubmittedCancels { get; } = new();

    public Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct)
    {
        SubmittedOrders.Add(request);
        return Task.FromResult(request.ClOrdID);
    }

    public Task CancelAsync(CancelOrderRequest request, CancellationToken ct)
    {
        SubmittedCancels.Add(request);
        return Task.CompletedTask;
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
    public Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<string> SubmitCrossAsync(NewOrderCrossRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task SendQuoteRequestAsync(QuoteRequestMessage request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task SendQuoteAsync(QuoteMessage request, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task CancelQuoteAsync(string quoteId, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task ConnectAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task TerminateAsync(TerminationCode code, CancellationToken ct) => throw new NotSupportedException();
    public Task FlushAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task ReconnectAsync(uint sessionVerId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ReconnectOutcome> ReconnectAsync(ReconnectMode mode, Func<uint, uint>? nextSessionVerIdSelector, CancellationToken ct) =>
        throw new NotSupportedException();
    public ClientHealth GetHealth() => throw new NotSupportedException();
    public IAsyncEnumerable<EntryPointEvent> Events(CancellationToken ct) => throw new NotSupportedException();
    public FixpClientState State => throw new NotSupportedException();
    public IList<IPreTradeGate> RiskGates => throw new NotSupportedException();
    public IKeepAliveScheduler KeepAlive => throw new NotSupportedException();
    public IRetransmitRequestHandler Retransmit => throw new NotSupportedException();
    public event EventHandler<TerminatedEventArgs>? Terminated
    {
        add { }
        remove { }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
