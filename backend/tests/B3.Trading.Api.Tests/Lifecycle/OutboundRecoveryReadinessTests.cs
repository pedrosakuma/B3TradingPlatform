using System.Net;
using System.Net.Http.Json;
using B3.Trading.Application.Outbound;
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
}
