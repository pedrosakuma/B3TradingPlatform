using B3.Trading.Domain;

using B3.Trading.Application;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Holds one <see cref="B3EntryPointClientGateway"/> per <see cref="FirmConfig"/>
/// and exposes:
/// <list type="bullet">
///   <item>per-firm lookup (<see cref="For"/>) used by <see cref="MultiFirmExchangeGateway"/>.</item>
///   <item>an aggregate <see cref="IEntryPointClient"/> facade that fans every
///         firm's ER stream into a single <c>ExecutionReportReceived</c> event,
///         so the existing <see cref="EntryPointExecutionReportRouter"/> can
///         consume all firms transparently.</item>
/// </list>
/// </summary>
public sealed class FirmGatewayRegistry : IEntryPointClient, IFirmSessionStatusProvider, IAsyncDisposable
{
    private readonly Dictionary<string, B3EntryPointClientGateway> _gateways;
    private readonly Action<ExecutionReportEnvelope> _bridge;

    public FirmGatewayRegistry(IEnumerable<B3EntryPointClientGateway> gateways)
    {
        _gateways = gateways.ToDictionary(g => g.FirmId, StringComparer.Ordinal);
        if (_gateways.Count == 0)
            throw new InvalidOperationException("FirmGatewayRegistry requires at least one configured firm.");
        _bridge = e => ExecutionReportReceived?.Invoke(e);
        foreach (var g in _gateways.Values)
            g.ExecutionReportReceived += _bridge;
    }

    public IReadOnlyDictionary<string, B3EntryPointClientGateway> Gateways => _gateways;

    public bool TryGet(string firmId, out B3EntryPointClientGateway? gateway)
    {
        var ok = _gateways.TryGetValue(firmId, out var g);
        gateway = g;
        return ok;
    }

    public B3EntryPointClientGateway For(string firmId) =>
        _gateways.TryGetValue(firmId, out var g)
            ? g
            : throw new KeyNotFoundException($"No EntryPoint gateway configured for firm '{firmId}'.");

    public Task ConnectAllAsync(CancellationToken ct) =>
        Task.WhenAll(_gateways.Values.Select(g => g.ConnectAsync(ct)));

    /// <inheritdoc />
    public IReadOnlyList<FirmSessionStatus> Snapshot()
    {
        var result = new FirmSessionStatus[_gateways.Count];
        var i = 0;
        foreach (var g in _gateways.Values)
        {
            // SessionStateTag and IsReconnecting both read volatile snapshots
            // off the gateway. SessionStateTag projects the SDK's live
            // FixpClientState, so a Suspended/Terminated session is
            // immediately visible to /health on the next request.
            result[i++] = new FirmSessionStatus(
                g.FirmId,
                g.SessionStateTag,
                g.IsReconnecting,
                g.CurrentSessionVerId);
        }
        return result;
    }

    public event Action<ExecutionReportEnvelope>? ExecutionReportReceived;

    // Submit-side surface unused — OrdersEndpoints injects IExchangeGateway,
    // which resolves to MultiFirmExchangeGateway (which dispatches by firmId).
    public Task SubmitNewOrderAsync(NewOrderSingle request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use IExchangeGateway.");
    public Task SubmitCancelAsync(OrderCancelRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use IExchangeGateway.");
    public Task SubmitCancelReplaceAsync(OrderCancelReplaceRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use IExchangeGateway.");

    public async ValueTask DisposeAsync()
    {
        foreach (var g in _gateways.Values)
            g.ExecutionReportReceived -= _bridge;
        foreach (var g in _gateways.Values)
            await g.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Single <see cref="IExchangeGateway"/> registration that dispatches to the
/// per-firm <see cref="B3EntryPointClientGateway"/> in
/// <see cref="FirmGatewayRegistry"/> based on <see cref="Order.FirmId"/>.
/// </summary>
public sealed class MultiFirmExchangeGateway : IExchangeGateway
{
    private readonly FirmGatewayRegistry _registry;

    public MultiFirmExchangeGateway(FirmGatewayRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task SubmitAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        return _registry.For(order.FirmId).SubmitAsync(order, cancellationToken);
    }

    public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        return _registry.For(order.FirmId).CancelAsync(order, newClOrdId, cancellationToken);
    }

    public Task CancelReplaceAsync(
        Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        return _registry.For(original.FirmId).CancelReplaceAsync(
            original, newClOrdId, newQuantity, newPrice,
            requestedTimeInForce, requestedStopPrice, requestedGoodTillDate, cancellationToken);
    }
}
