using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Scheduling;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B3.Trading.Api.Tests.Lifecycle;

public sealed class OutboundRecoveryReadinessTests
{
    [Fact]
    public async Task SecondHostFenceLoser_RemainsLiveButNeverReadyOrEpochInitialised()
    {
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-artifacts",
            "fence-loser-" + Guid.NewGuid().ToString("N"));
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Persistence:Enabled"] = "true",
            ["Trading:Persistence:DataDirectory"] = dataDir,
            ["Trading:Persistence:FirmId"] = "deployment",
            ["Trading:Persistence:FsyncOnFlush"] = "false",
        };

        try
        {
            using (var winner = TestAppFactory.WithOverrides(overrides))
            using (var winnerClient = winner.CreateClient())
            {
                await winner.Services
                    .GetRequiredService<IOutboundRecoveryGate>()
                    .WaitUntilClassificationCompleteAsync(CancellationToken.None);

                using var loser = TestAppFactory.WithOverrides(overrides);
                using var loserClient = loser.CreateClient();

                Assert.Equal(HttpStatusCode.OK, (await loserClient.GetAsync("/live")).StatusCode);
                Assert.Equal(
                    HttpStatusCode.ServiceUnavailable,
                    (await loserClient.GetAsync("/ready")).StatusCode);
                Assert.Equal(
                    OutboundRecoveryPhase.FenceUnavailable,
                    loser.Services.GetRequiredService<IOutboundRecoveryGate>().Phase);
                Assert.False(
                    loser.Services
                        .GetRequiredService<OutboundProcessEpoch>()
                        .IsInitialized);
                Assert.IsType<B3.Trading.Infrastructure.Persistence.FaultedReconciliationMarkerStore>(
                    loser.Services.GetRequiredService<
                        B3.Trading.Application.Persistence.IReconciliationMarkerStore>());
            }
        }
        finally
        {
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_SeedsRecoveredGtdOrderAfterPersistenceRecovery_AndExpiresIt()
    {
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-artifacts",
            "gtd-recovery-" + Guid.NewGuid().ToString("N"));
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Persistence:Enabled"] = "true",
            ["Trading:Persistence:DataDirectory"] = dataDir,
            ["Trading:Persistence:FirmId"] = "default",
            ["Trading:Persistence:FsyncOnFlush"] = "false",
            ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
        };
        const ulong clOrdId = 8_001;

        try
        {
            using (var first = TestAppFactory.WithOverrides(overrides))
            using (var firstClient = first.CreateClient())
            {
                await first.Services
                    .GetRequiredService<IOutboundRecoveryGate>()
                    .WaitUntilClassificationCompleteAsync(CancellationToken.None);

                var owner = first.Services
                    .GetRequiredService<EndClientRegistry>()
                    .Register(TestAppFactory.TestUser);
                var order = new Order(
                    clOrdId,
                    owner,
                    "PETR4",
                    4321,
                    OrderSide.Buy,
                    OrderType.Limit,
                    100,
                    30m,
                    firmId: "FIRM01",
                    timeInForce: TimeInForce.GTD,
                    goodTillDate: DateTimeOffset.UtcNow.AddSeconds(3));
                order.MarkWorking();
                Assert.True(first.Services.GetRequiredService<WorkingOrderBook>().TryAdd(order));
                first.Services.GetRequiredService<OrderOwnershipMap>().Register(clOrdId, owner);
                Assert.True(await first.Services
                    .GetRequiredService<SnapshotService>()
                    .TryTakeSnapshotAsync());
            }

            using var second = TestAppFactory.WithOverrides(overrides);
            using var secondClient = second.CreateClient();
            await second.Services
                .GetRequiredService<IOutboundRecoveryGate>()
                .WaitUntilClassificationCompleteAsync(CancellationToken.None);
            var scheduler = second.Services.GetRequiredService<GtdExpirationScheduler>();

            await WaitUntilAsync(
                () => scheduler.TrackedCount == 1,
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => scheduler.TrackedCount == 0,
                TimeSpan.FromSeconds(8));

            var eventStore = second.Services.GetRequiredService<IEventStore>();
            await eventStore.FlushAsync();
            var expiredPersisted = false;
            await foreach (var (_, evt) in eventStore.ReadFromAsync(0))
                expiredPersisted |= evt is OrderExpiredEvent { ClOrdId: clOrdId };
            Assert.True(expiredPersisted);
        }
        finally
        {
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryGate_KeepsLiveAvailable_ReadyClosed_AndRejectsRestMutation()
    {
        var gate = new ClosedRecoveryGate();
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IOutboundRecoveryGate>();
                services.AddSingleton<IOutboundRecoveryGate>(gate);
            });
        using var client = await factory.CreateAuthedClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/live")).StatusCode);
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/ready")).StatusCode);

        var response = await client.PostAsJsonAsync("/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 12345UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ClosedRecoveryGate_RejectsAlgoAndOrderMutationsBeforeStateAccess()
    {
        var gate = new ClosedRecoveryGate();
        var signals = new RecordingAlgoSignalQueue();
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IOutboundRecoveryGate>();
                services.AddSingleton<IOutboundRecoveryGate>(gate);
                services.RemoveAll<IAlgoSignalQueue>();
                services.AddSingleton<IAlgoSignalQueue>(signals);
            });
        using var client = await factory.CreateAuthedClientAsync();
        var eventStore = factory.Services.GetRequiredService<IEventStore>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var algos = factory.Services.GetRequiredService<AlgoBook>();
        Assert.True(algos.TryAdd(new Algo(
            999,
            registry.Register(TestAppFactory.TestUser),
            "FIRM01",
            "PETR4",
            4321,
            OrderSide.Buy,
            AlgoType.Iceberg,
            100,
            new IcebergParameters(10, 30m),
            DateTimeOffset.UtcNow)));
        var seqBefore = eventStore.CurrentSeq;

        var createAlgo = await client.PostAsJsonAsync("/algo", new CreateAlgoRequest(
            "PETR4",
            4321,
            "Buy",
            "Iceberg",
            100,
            new CreateAlgoIcebergParams(10, 30m),
            Twap: null));
        var modifyAlgo = await client.PostAsJsonAsync(
            "/algo/999/modify",
            new ModifyAlgoRequest(NewQuantity: 10));
        var cancelAlgo = await client.DeleteAsync("/algo/999");
        var modifyOrder = await client.PutAsJsonAsync(
            "/orders/999",
            new ModifyOrderRequest(Quantity: 10, Price: 30m));
        var cancelOrder = await client.DeleteAsync("/orders/999");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, createAlgo.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, modifyAlgo.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, cancelAlgo.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, modifyOrder.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, cancelOrder.StatusCode);
        Assert.Equal(seqBefore, eventStore.CurrentSeq);
        Assert.Equal(0, signals.EnqueueCount);
    }

    private sealed class ClosedRecoveryGate : IOutboundRecoveryGate
    {
        public OutboundRecoveryPhase Phase => OutboundRecoveryPhase.RestoringPersistence;
        public bool IsClassificationComplete => false;
        public bool IsReady => false;
        public string? FailureReason => null;

        public IReadOnlyList<FirmOutboundRecoveryStatus> Snapshot() =>
            [new("TEST", true, false, 0)];

        public bool IsBusinessIngressOpen(string firmId) => false;

        public async ValueTask WaitUntilClassificationCompleteAsync(
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public async ValueTask WaitUntilBusinessIngressOpenAsync(
            string firmId,
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public async ValueTask WaitUntilAllRequiredBusinessIngressOpenAsync(
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class RecordingAlgoSignalQueue : IAlgoSignalQueue
    {
        public int EnqueueCount { get; private set; }

        public bool TryEnqueue(AlgoSignal signal)
        {
            EnqueueCount++;
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition());
    }
}
