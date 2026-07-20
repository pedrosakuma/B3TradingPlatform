using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B3.Trading.Api.Tests;

public sealed class OrderIdempotencyEndpointTests
{
    [Fact]
    public async Task SameKeyAndCanonicalBody_ReplaysExistingMutation()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        using var first = await PostAsync(http, token, "same-key", Body());
        using var second = await PostAsync(
            http,
            token,
            "same-key",
            Body(side: "buy", type: "limit", timeInForce: "day"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstJson = await ReadAsync(first);
        var secondJson = await ReadAsync(second);
        Assert.Equal(firstJson.MutationId, secondJson.MutationId);
        Assert.Equal(firstJson.ClOrdId, secondJson.ClOrdId);
        Assert.False(firstJson.Replayed);
        Assert.True(secondJson.Replayed);
    }

    [Fact]
    public async Task DefaultedAndExplicitEffectiveFields_HashIdentically()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);
        var defaulted = new
        {
            symbol = "PETR4",
            securityId = 4321,
            side = "Buy",
            type = "Limit",
            quantity = 100,
            price = 30m,
            displayQty = 10,
        };
        var explicitBody = new
        {
            symbol = "PETR4",
            securityId = 4321,
            side = "Buy",
            type = "Limit",
            quantity = 100,
            price = 30m,
            timeInForce = "Day",
            displayQty = 10,
            displayResetPolicy = "Always",
        };

        using var first = await PostAsync(http, token, "defaults-key", defaulted);
        using var second = await PostAsync(http, token, "defaults-key", explicitBody);
        var firstPayload = await ReadAsync(first);
        var secondPayload = await ReadAsync(second);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(firstPayload.MutationId, secondPayload.MutationId);
        Assert.True(secondPayload.Replayed);
    }

    [Fact]
    public async Task SameKeyDifferentBody_ReturnsConflict()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        using var first = await PostAsync(http, token, "conflict-key", Body(quantity: 100));
        using var second = await PostAsync(http, token, "conflict-key", Body(quantity: 101));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task ConcurrentSameKey_CreatesExactlyOneMutation()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => PostAsync(http, token, "concurrent-key", Body()))
            .ToArray();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            var payloads = await Task.WhenAll(responses.Select(ReadAsync));
            Assert.Single(payloads.Select(x => x.MutationId).Distinct());
            Assert.Single(payloads.Select(x => x.ClOrdId).Distinct());
            Assert.Equal(1, payloads.Count(x => !x.Replayed));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task SameKeyAcrossPrincipals_DoesNotCollideOrDiscloseLookup()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var alice = await factory.LoginAsync(http);
        var bob = await factory.LoginAsync(http, "bob", TestAppFactory.TestPassword);

        using var aliceResponse = await PostAsync(http, alice, "principal-key", Body());
        using var bobResponse = await PostAsync(http, bob, "principal-key", Body());
        var alicePayload = await ReadAsync(aliceResponse);
        var bobPayload = await ReadAsync(bobResponse);

        Assert.NotEqual(alicePayload.MutationId, bobPayload.MutationId);
        using var lookup = new HttpRequestMessage(
            HttpMethod.Get,
            $"/orders/mutations/{alicePayload.MutationId}");
        lookup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bob);
        using var lookupResponse = await http.SendAsync(lookup);
        Assert.Equal(HttpStatusCode.NotFound, lookupResponse.StatusCode);
    }

    [Fact]
    public async Task MissingKey_IsAcceptedWithRolloutWarning()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders/")
        {
            Content = JsonContent.Create(Body()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Idempotency-Key-Required").Single());
        Assert.True(response.Headers.Contains("Warning"));
    }

    [Fact]
    public async Task KeyIsNeverReturnedInResponse()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);
        const string key = "sensitive-key-never-echo";

        using var response = await PostAsync(http, token, key, Body());
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(key, body, StringComparison.Ordinal);
        Assert.All(
            response.Headers.SelectMany(h => h.Value),
            value => Assert.DoesNotContain(key, value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameKeyAfterRestart_ReplaysDurableBinding()
    {
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-artifacts",
            "idempotency-" + Guid.NewGuid().ToString("N"));
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Persistence:Enabled"] = "true",
            ["Trading:Persistence:DataDirectory"] = dataDir,
            ["Trading:Persistence:FirmId"] = "default",
            ["Trading:Persistence:FsyncOnFlush"] = "false",
            ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
        };
        try
        {
            ResponsePayload firstPayload;
            using (var firstFactory = TestAppFactory.WithOverrides(overrides))
            using (var firstHttp = firstFactory.CreateClient())
            {
                var token = await firstFactory.LoginAsync(firstHttp);
                using var first = await PostAsync(firstHttp, token, "restart-key", Body());
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
                firstPayload = await ReadAsync(first);
            }

            using var secondFactory = TestAppFactory.WithOverrides(
                overrides,
                UseImmediateRecoveryGate);
            using var secondHttp = secondFactory.CreateClient();
            await secondFactory.Services
                .GetRequiredService<OutboundRecoveryState>()
                .WaitUntilClassificationCompleteAsync(CancellationToken.None);
            var secondToken = await secondFactory.LoginAsync(secondHttp);
            using var second = await PostAsync(secondHttp, secondToken, "restart-key", Body());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            var secondPayload = await ReadAsync(second);
            Assert.Equal(firstPayload.MutationId, secondPayload.MutationId);
            Assert.Equal(firstPayload.ClOrdId, secondPayload.ClOrdId);
            Assert.True(secondPayload.Replayed);
        }
        finally
        {
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ApprovalCommitFailure_ReplayReturnsDurableNoWriteRejection()
    {
        var store = new RejectingApprovalStore();
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IEventStore>();
                services.AddSingleton<IEventStore>(store);
                services.RemoveAll<IEventStoreHealth>();
                services.AddSingleton<IEventStoreHealth>(store);
            });
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        using var first = await PostAsync(http, token, "approval-failure-key", Body());
        using var second = await PostAsync(http, token, "approval-failure-key", Body());
        var firstPayload = await ReadAsync(first);
        var secondPayload = await ReadAsync(second);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(firstPayload.MutationId, secondPayload.MutationId);
        Assert.Equal(firstPayload.ClOrdId, secondPayload.ClOrdId);
        Assert.Equal("Rejected", firstPayload.Status);
        Assert.Equal("Rejected", secondPayload.Status);
        Assert.True(secondPayload.Replayed);
        Assert.DoesNotContain(store.Events, evt => evt is OutboundApprovedEvent);
        Assert.Contains(
            store.Events,
            evt => evt is ExecutionReportReceivedEvent { OutboundProvenNoWrite: true });
    }

    [Fact]
    public async Task ExistingBinding_ReplaysAfterSubAccountDeactivationBeforeMutableValidation()
    {
        using var factory = new TestAppFactory();
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);
        var registry = factory.Services.GetRequiredService<SubAccountsRegistry>();
        foreach (var firm in new[] { "default", "FIRM01", "TEST" })
            registry.ApplyCreated(firm, "desk-1", "Desk 1");
        var body = new
        {
            symbol = "PETR4",
            securityId = 4321,
            side = "Buy",
            type = "Limit",
            quantity = 100,
            price = 30m,
            subAccountId = "desk-1",
        };

        using var first = await PostAsync(http, token, "subaccount-key", body);
        foreach (var firm in new[] { "default", "FIRM01", "TEST" })
            registry.ApplyDeactivated(firm, "desk-1");
        using var replay = await PostAsync(http, token, "subaccount-key", body);
        using var conflict = await PostAsync(
            http,
            token,
            "subaccount-key",
            new
            {
                symbol = "PETR4",
                securityId = 4321,
                side = "Buy",
                type = "Limit",
                quantity = 101,
                price = 30m,
                subAccountId = "desk-1",
            });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.True((await ReadAsync(replay)).Replayed);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task StableReferenceRotation_ReplaysWithHistory_AndFailsClosedWithoutIt()
    {
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-artifacts",
            "idempotency-rotation-" + Guid.NewGuid().ToString("N"));
        var persistence = new Dictionary<string, string?>
        {
            ["Trading:Persistence:Enabled"] = "true",
            ["Trading:Persistence:DataDirectory"] = dataDir,
            ["Trading:Persistence:FirmId"] = "default",
            ["Trading:Persistence:FsyncOnFlush"] = "false",
            ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
        };
        try
        {
            var oldConfig = ProtectionConfig("old", ["old"]);
            foreach (var pair in persistence)
                oldConfig[pair.Key] = pair.Value;
            ResponsePayload original;
            using (var factory = TestAppFactory.WithOverrides(oldConfig))
            using (var http = factory.CreateClient())
            {
                await factory.Services
                    .GetRequiredService<OutboundRecoveryState>()
                    .WaitUntilClassificationCompleteAsync(CancellationToken.None);
                var token = await factory.LoginAsync(http);
                using var response = await PostAsync(http, token, "rotation-api-key", Body());
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                original = await ReadAsync(response);
            }

            var retainedConfig = ProtectionConfig("new", ["old", "new"]);
            foreach (var pair in persistence)
                retainedConfig[pair.Key] = pair.Value;
            using (var factory = TestAppFactory.WithOverrides(
                       retainedConfig,
                       UseImmediateRecoveryGate))
            using (var http = factory.CreateClient())
            {
                await factory.Services
                    .GetRequiredService<OutboundRecoveryState>()
                    .WaitUntilClassificationCompleteAsync(CancellationToken.None);
                var token = await factory.LoginAsync(http);
                using var response = await PostAsync(http, token, "rotation-api-key", Body());
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var replayed = await ReadAsync(response);
                Assert.Equal(original.MutationId, replayed.MutationId);
                Assert.Equal(original.ClOrdId, replayed.ClOrdId);
                Assert.True(replayed.Replayed);
            }

            var missingConfig = ProtectionConfig("new", ["new"]);
            foreach (var pair in persistence)
                missingConfig[pair.Key] = pair.Value;
            using var missingFactory = TestAppFactory.WithOverrides(
                missingConfig,
                UseImmediateRecoveryGate);
            using var missingHttp = missingFactory.CreateClient();
            await missingFactory.Services
                .GetRequiredService<OutboundRecoveryState>()
                .WaitUntilClassificationCompleteAsync(CancellationToken.None);
            var missingToken = await missingFactory.LoginAsync(missingHttp);
            using var unavailable = await PostAsync(
                missingHttp,
                missingToken,
                "rotation-api-key",
                Body());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            var unavailableJson = await unavailable.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "idempotency_history_unavailable",
                unavailableJson.GetProperty("code").GetString());
        }
        finally
        {
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProvenUnsentTerminal_ReplaysAsRejectedAfterRestart()
    {
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-artifacts",
            "proven-unsent-" + Guid.NewGuid().ToString("N"));
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Persistence:Enabled"] = "true",
            ["Trading:Persistence:DataDirectory"] = dataDir,
            ["Trading:Persistence:FirmId"] = "default",
            ["Trading:Persistence:FsyncOnFlush"] = "false",
            ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
        };
        static void ReplaceGateway(IServiceCollection services)
        {
            services.RemoveAll<IExchangeGateway>();
            services.AddSingleton<IExchangeGateway, ProvenUnsentApiGateway>();
        }
        try
        {
            ResponsePayload firstPayload;
            using (var factory = TestAppFactory.WithOverrides(overrides, ReplaceGateway))
            using (var http = factory.CreateClient())
            {
                var token = await factory.LoginAsync(http);
                using var first = await PostAsync(http, token, "proven-unsent-key", Body());
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
                firstPayload = await ReadAsync(first);
                Assert.Equal("Rejected", firstPayload.Status);
            }

            using var restartedFactory = TestAppFactory.WithOverrides(overrides, ReplaceGateway);
            using var restartedHttp = restartedFactory.CreateClient();
            var restartedToken = await restartedFactory.LoginAsync(restartedHttp);
            using var replay = await PostAsync(
                restartedHttp,
                restartedToken,
                "proven-unsent-key",
                Body());
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            var replayed = await ReadAsync(replay);
            Assert.Equal(firstPayload.MutationId, replayed.MutationId);
            Assert.Equal(firstPayload.ClOrdId, replayed.ClOrdId);
            Assert.Equal("Rejected", replayed.Status);
            Assert.True(replayed.Replayed);
        }
        finally
        {
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient http,
        string token,
        string key,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders/")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return await http.SendAsync(request);
    }

    private static object Body(
        long quantity = 100,
        string side = "Buy",
        string type = "Limit",
        string timeInForce = "Day") => new
        {
            symbol = "PETR4",
            securityId = 4321,
            side,
            type,
            quantity,
            price = 30m,
            timeInForce,
        };

    private static Dictionary<string, string?> ProtectionConfig(
        string activeKeyId,
        string[] keyIds)
    {
        var config = new Dictionary<string, string?>
        {
            ["Trading:OutboundCommandProtection:ActiveKeyId"] = activeKeyId,
            ["Trading:OutboundCommandProtection:ActiveKeyVersion"] = "1",
            ["Trading:OutboundCommandProtection:StableReferenceKeyId"] = activeKeyId,
            ["Trading:OutboundCommandProtection:StableReferenceKeyVersion"] = "1",
        };
        for (var i = 0; i < keyIds.Length; i++)
        {
            var keyId = keyIds[i];
            config[$"Trading:OutboundCommandProtection:Keys:{i}:KeyId"] = keyId;
            config[$"Trading:OutboundCommandProtection:Keys:{i}:Version"] = "1";
            config[$"Trading:OutboundCommandProtection:Keys:{i}:KeyBase64"] =
                Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"api-idempotency-rotation:{keyId}")));
        }
        return config;
    }

    private static async Task<ResponsePayload> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new ResponsePayload(
            json.GetProperty("mutationId").GetString()!,
            json.GetProperty("clOrdId").GetString()!,
            json.GetProperty("replayed").GetBoolean(),
            json.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null
                ? status.GetString()
                : null);
    }

    private sealed record ResponsePayload(
        string MutationId,
        string ClOrdId,
        bool Replayed,
        string? Status);

    private static void UseImmediateRecoveryGate(IServiceCollection services)
    {
        services.RemoveAll<IOutboundRecoveryGate>();
        services.AddSingleton<IOutboundRecoveryGate>(ImmediateOutboundRecoveryGate.Instance);
    }

    private sealed class RejectingApprovalStore : IEventStore, IEventStoreHealth
    {
        private long _seq;
        public List<WalEvent> Events { get; } = new();
        public long CurrentSeq => _seq;
        public long LastCommittedSeq => _seq;
        public bool IsHealthy => true;
        public Exception? TerminalFault => null;

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            if (evt is OutboundApprovedEvent)
                throw new WalBackpressureException("approval commit rejected");
            Events.Add(evt);
            return ++_seq;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask FlushThroughAsync(long seq, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProvenUnsentApiGateway : IExchangeGateway
    {
        public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
            Task.FromException(new ExchangeGatewayPreSendException("proven no-write"));

        public Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken) =>
            Task.FromException<ExchangeGatewayReceipt>(
                new ExchangeGatewayAttemptException(
                    "proven no-write",
                    ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
                    ExchangeGatewayAttemptStage.SequenceReservedAndEncoded,
                    frame: null));

        public Task CancelAsync(
            Order order,
            ulong newClOrdId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
