using System.Net;
using System.Text.Json;
using B3.Trading.SampleBot;

namespace B3.Trading.SampleBot.Tests;

public sealed class TradingPlatformRestClientTests
{
    [Fact]
    public async Task SubmitLimitOrder_SendsBearerIdempotencyAndSubAccount()
    {
        var handler = new StubHttpMessageHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/orders", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("internal-jwt", request.Headers.Authorization?.Parameter);
            Assert.Equal("submit-key", request.Headers.GetValues("Idempotency-Key").Single());

            var json = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal("PETR4", root.GetProperty("symbol").GetString());
            Assert.Equal("Buy", root.GetProperty("side").GetString());
            Assert.Equal("Limit", root.GetProperty("type").GetString());
            Assert.Equal("ACC-01", root.GetProperty("subAccountId").GetString());

            return StubHttpMessageHandler.Json(
                HttpStatusCode.Accepted,
                """
                {"mutationId":"m1","clOrdId":"101","state":"RecordedPendingApproval","lookupUrl":"/api/orders/mutations/m1","replayed":false,"status":null,"reason":null,"code":null,"error":null}
                """);
        });

        var client = CreateClient(handler);
        var result = await client.SubmitLimitOrderAsync(new SubmitOrderCommand("PETR4", 0, "Buy", 100, 30m, "ACC-01"), "submit-key", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        Assert.Equal("101", result.Payload!.ClOrdId);
        Assert.Equal("RecordedPendingApproval", result.Payload.State);
    }

    [Fact]
    public async Task CancelOrder_SendsDeleteWithIdempotencyKey()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/api/orders/101", request.RequestUri!.AbsolutePath);
            Assert.Equal("cancel-key", request.Headers.GetValues("Idempotency-Key").Single());
            return Task.FromResult(StubHttpMessageHandler.Json(
                HttpStatusCode.Accepted,
                """
                {"mutationId":"m2","clOrdId":"202","state":"RecordedPendingApproval","lookupUrl":"/api/orders/mutations/m2","replayed":false,"status":null,"reason":null,"code":null,"error":null}
                """));
        });

        var client = CreateClient(handler);
        var result = await client.CancelOrderAsync("101", "cancel-key", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        Assert.Equal("m2", result.Payload!.MutationId);
    }

    private static TradingPlatformRestClient CreateClient(HttpMessageHandler handler)
    {
        var sessionCache = new AuthenticatedSessionCache(
            new StubAuthProvider(new AuthenticatedSession("internal-jwt", null, SampleBotAuthMode.InternalToken)),
            TimeProvider.System);
        return new TradingPlatformRestClient(new HttpClient(handler) { BaseAddress = new Uri("https://trading.local") }, sessionCache);
    }
}
