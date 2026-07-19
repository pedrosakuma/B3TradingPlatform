using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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

            using var secondFactory = TestAppFactory.WithOverrides(overrides);
            using var secondHttp = secondFactory.CreateClient();
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

    private static async Task<ResponsePayload> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new ResponsePayload(
            json.GetProperty("mutationId").GetString()!,
            json.GetProperty("clOrdId").GetString()!,
            json.GetProperty("replayed").GetBoolean());
    }

    private sealed record ResponsePayload(
        string MutationId,
        string ClOrdId,
        bool Replayed);
}
