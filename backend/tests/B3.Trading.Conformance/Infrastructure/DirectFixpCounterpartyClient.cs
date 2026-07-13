using System.Collections.Concurrent;
using System.Net;
using B3.EntryPoint.Client;
using UpModels = B3.EntryPoint.Client.Models;
using UpState = B3.EntryPoint.Client.State;

namespace B3.Trading.Conformance.Infrastructure;

internal sealed class DirectFixpCounterpartyClient : IAsyncDisposable
{
    private readonly EntryPointClient _client;
    private readonly string _stateDirectory;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _receiveLoop;
    private readonly ConcurrentDictionary<ulong, PendingOrder> _orders = new();
    private long _nextClOrdId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20;

    private DirectFixpCounterpartyClient(EntryPointClient client, string stateDirectory)
    {
        _client = client;
        _stateDirectory = stateDirectory;
        _receiveLoop = ReceiveLoopAsync(_disposeCts.Token);
    }

    public static async Task<DirectFixpCounterpartyClient> ConnectAsync(
        string endpoint,
        uint sessionId,
        uint sessionVerId,
        uint enteringFirm,
        string accessKey,
        string senderLocation,
        string enteringTrader,
        CancellationToken ct = default)
    {
        var parsed = ParseEndpoint(endpoint);
        var addresses = await Dns.GetHostAddressesAsync(parsed.Host, ct);
        if (addresses.Length == 0)
            throw new InvalidOperationException($"Could not resolve FIXP endpoint host '{parsed.Host}'.");

        var stateDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "ConformanceState",
            "direct-fixp-counterparty",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);

        var options = new EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(addresses[0], parsed.Port),
            SessionId = sessionId,
            SessionVerId = sessionVerId,
            EnteringFirm = enteringFirm,
            ConnectMode = ConnectMode.EstablishReuseThenNegotiate,
            Credentials = EntryPointClientOptions.AccessKey(accessKey),
            SenderLocation = senderLocation,
            EnteringTrader = enteringTrader,
            SessionStateStore = new UpState.FileSessionStateStore(stateDirectory),
        };

        var client = new EntryPointClient(options);
        try
        {
            await client.ConnectAsync(ct);
            return new DirectFixpCounterpartyClient(client, stateDirectory);
        }
        catch
        {
            await client.DisposeAsync();
            TryDeleteDirectory(stateDirectory);
            throw;
        }
    }

    public async Task<ulong> SubmitLimitAsync(
        ulong securityId,
        bool isBuy,
        decimal price,
        long quantity,
        CancellationToken ct = default)
    {
        var clOrdId = (ulong)Interlocked.Increment(ref _nextClOrdId);
        _orders[clOrdId] = new PendingOrder(clOrdId, quantity);

        var request = new UpModels.NewOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(clOrdId),
            SecurityId = securityId,
            Side = isBuy ? UpModels.Side.Buy : UpModels.Side.Sell,
            OrderType = UpModels.OrderType.Limit,
            Price = price,
            OrderQty = (ulong)quantity,
            TimeInForce = UpModels.TimeInForce.Day,
        };

        await _client.SubmitAsync(request, ct);
        return clOrdId;
    }

    public async Task<CounterpartyTradeSnapshot> WaitForFilledAsync(
        ulong clOrdId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!_orders.TryGetValue(clOrdId, out var pending))
            throw new InvalidOperationException($"Counterparty order {clOrdId} was not registered.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        timeoutCts.CancelAfter(timeout);
        return await pending.WaitForFilledAsync(timeoutCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        try
        {
            await _receiveLoop;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        await _client.DisposeAsync();
        TryDeleteDirectory(_stateDirectory);
        _disposeCts.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        await foreach (var ev in _client.Events(ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case UpModels.OrderTrade trade when _orders.TryGetValue(trade.ClOrdID.Value, out var traded):
                    traded.OnTrade(
                        cumulativeQuantity: (long)(trade.CumQty ?? 0UL),
                        leavesQuantity: (long)(trade.LeavesQty ?? 0UL));
                    break;

                case UpModels.OrderRejected rejected when _orders.TryGetValue(rejected.ClOrdID.Value, out var rejectedOrder):
                    rejectedOrder.Fail($"Counterparty order {rejected.ClOrdID.Value} was rejected.");
                    break;

                case UpModels.OrderCancelled cancelled when _orders.TryGetValue(cancelled.ClOrdID.Value, out var cancelledOrder):
                    cancelledOrder.Fail($"Counterparty order {cancelled.ClOrdID.Value} was cancelled before filling.");
                    break;
            }
        }
    }

    private static DnsEndPoint ParseEndpoint(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1)
            throw new ArgumentException($"Endpoint '{endpoint}' must be host:port.", nameof(endpoint));

        var host = endpoint[..colon];
        if (!int.TryParse(endpoint[(colon + 1)..], out var port) || port is <= 0 or > 65535)
            throw new ArgumentException($"Endpoint '{endpoint}' has invalid port.", nameof(endpoint));

        return new DnsEndPoint(host, port);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class PendingOrder
    {
        private readonly long _expectedQuantity;
        private readonly TaskCompletionSource<CounterpartyTradeSnapshot> _filled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingOrder(ulong clOrdId, long expectedQuantity)
        {
            ClOrdId = clOrdId;
            _expectedQuantity = expectedQuantity;
        }

        public ulong ClOrdId { get; }

        public void OnTrade(long cumulativeQuantity, long leavesQuantity)
        {
            if (cumulativeQuantity >= _expectedQuantity || leavesQuantity == 0)
            {
                _filled.TrySetResult(new CounterpartyTradeSnapshot(
                    ClOrdId,
                    cumulativeQuantity,
                    leavesQuantity));
            }
        }

        public void Fail(string message) =>
            _filled.TrySetException(new InvalidOperationException(message));

        public Task<CounterpartyTradeSnapshot> WaitForFilledAsync(CancellationToken ct)
        {
            ct.Register(static state =>
            {
                var tcs = (TaskCompletionSource<CounterpartyTradeSnapshot>)state!;
                tcs.TrySetCanceled();
            }, _filled);

            return _filled.Task;
        }
    }
}

internal sealed record CounterpartyTradeSnapshot(
    ulong ClOrdId,
    long CumulativeQuantity,
    long LeavesQuantity);
