using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Outbound;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B3.Trading.Api.Tests.Lifecycle;

public class FailClosedIngressTests
{
    [Fact]
    public async Task GatewayAmbiguity_ReturnsClOrdIdAndDrainsWithoutSyntheticRejection()
    {
        var store = new SyntheticTerminalRejectingStore();
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IEventStore>();
                services.AddSingleton<IEventStore>(store);
                services.RemoveAll<IEventStoreHealth>();
                services.AddSingleton<IEventStoreHealth>(store);
                services.RemoveAll<IExchangeGateway>();
                services.AddSingleton<IExchangeGateway>(new ThrowingGateway());
            });
        using var client = await factory.CreateAuthedClientAsync();

        var response = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(rawBody);
        Assert.True(body.RootElement.TryGetProperty("code", out var code), rawBody);
        Assert.Equal("wal_reconciliation_required", code.GetString());
        Assert.True(body.RootElement.TryGetProperty("clOrdId", out var clOrdId), rawBody);
        Assert.NotEqual("0", clOrdId.GetString());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/live")).StatusCode);

        using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
        Assert.Equal(
            "outbound_new_order_reconciliation_required",
            health.RootElement.GetProperty("drainReason").GetString());
        Assert.DoesNotContain(
            store.Events,
            evt => evt is ExecutionReportReceivedEvent { Synthetic: true });
    }

    private sealed class SyntheticTerminalRejectingStore : IEventStore, IEventStoreHealth
    {
        private long _seq;
        public List<WalEvent> Events { get; } = new();
        public long CurrentSeq => Interlocked.Read(ref _seq);
        public bool IsHealthy => true;
        public Exception? TerminalFault => null;

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            Events.Add(evt);
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingGateway : IExchangeGateway
    {
        public Task SubmitAsync(Order order, CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("venue unavailable"));
        public async Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken ct)
        {
            await onFramePrepared(
                new ExchangeGatewayFrameIdentity(
                    command.FirmId,
                    1,
                    1,
                    1,
                    ExchangeGatewayOperation.NewOrder,
                    command.Canonical.ClOrdId,
                    1,
                    new string('a', 64)),
                ct);
            throw new IOException("socket outcome unknown");
        }
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken ct) => Task.CompletedTask;
    }
}
