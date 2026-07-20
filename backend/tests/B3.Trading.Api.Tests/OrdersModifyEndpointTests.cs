using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Slice 4 of #122 — HTTP integration coverage for
/// <c>PUT /orders/{clOrdId}</c>. Asserts the mapping from
/// <see cref="B3.Trading.Application.OrderModifyResultKind"/> values
/// to status codes and the side-effect contract (in-flight guard,
/// owner-isolation, cum-quantity floor).
/// </summary>
public class OrdersModifyEndpointTests
{
    [Fact]
    public async Task PUT_orders_HappyPath_Returns202WithNewClOrdId()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var put = await PutModify(http, token, origAck!.ClOrdId, qty: 200, price: 30m);
        var bodyText = await put.Content.ReadAsStringAsync();
        Assert.True(put.StatusCode == HttpStatusCode.Accepted, $"Expected 202, got {put.StatusCode}: {bodyText}");
        var ack = System.Text.Json.JsonSerializer.Deserialize<ModifyAck>(bodyText,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(ack);
        Assert.Equal(origAck.ClOrdId, ack!.OriginalClOrdId);
        Assert.NotEqual(origAck.ClOrdId, ack.ClOrdId);
        Assert.False(string.IsNullOrEmpty(ack.ClOrdId));
    }

    [Fact]
    public async Task PUT_orders_InvalidClOrdIdFormat_Returns404()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var put = await PutModify(http, token, "not-a-number", qty: 200, price: 30m);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_UnknownClOrdId_Returns404()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // The ClOrdID space is per-end-client and starts non-zero; a
        // very large number will never have been generated.
        var put = await PutModify(http, token, "99999999999999999", qty: 200, price: 30m);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_OwnerMismatch_Returns404_NoCrossOwnerLeak()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var aliceToken = await f.LoginAsync(http);
        var posted = await PostOrder(http, aliceToken, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        // bob is a separately-seeded user (env-seeded alongside alice).
        var bobToken = await f.LoginAsync(http, user: "bob", password: "wonderland");
        var put = await PutModify(http, bobToken, origAck!.ClOrdId, qty: 200, price: 30m);
        // Same status as a non-existent order — do not leak existence
        // across owners.
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_ZeroQuantity_Returns400()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var put = await PutModify(http, token, origAck!.ClOrdId, qty: 0, price: 30m);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_SecondModifyForSameOrig_Returns409()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await PutModify(http, token, origAck!.ClOrdId, qty: 200, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // Mock gateway just queues the cancel-replace request; no ER
        // arrives, so the in-flight intent stays pending.
        var second = await PutModify(http, token, origAck.ClOrdId, qty: 250, price: 30m);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_TimeInForceAsString_AcceptsCaseInsensitive()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var future = DateTimeOffset.UtcNow.AddDays(7);
        var req = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origAck!.ClOrdId}")
        {
            Content = JsonContent.Create(new
            {
                Quantity = 200,
                Price = 30m,
                TimeInForce = "gtd", // lowercase string — must parse to TimeInForce.GTD
                GoodTillDate = future,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var put = await http.SendAsync(req);
        var body = await put.Content.ReadAsStringAsync();

        Assert.True(put.StatusCode == HttpStatusCode.Accepted, $"Expected 202, got {put.StatusCode}: {body}");
    }

    [Fact]
    public async Task PUT_orders_InvalidTimeInForceString_Returns400_WithReason()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var req = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origAck!.ClOrdId}")
        {
            Content = JsonContent.Create(new { Quantity = 200, Price = 30m, TimeInForce = "Garbage" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var put = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var body = await put.Content.ReadAsStringAsync();
        Assert.Contains("timeInForce", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Garbage", body);
    }

    [Fact]
    public async Task PUT_orders_OmittedTimeInForce_TreatedAsNoChange()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        // Body literally omits TimeInForce — must accept and not blow up.
        var req = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origAck!.ClOrdId}")
        {
            Content = JsonContent.Create(new { Quantity = 200, Price = 30m }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var put = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Accepted, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_IdempotentRepeat_ReplaysMutationWithoutSecondAttempt()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var original = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await PutModify(
            http,
            token,
            original!.ClOrdId,
            qty: 200,
            price: 30m,
            idempotencyKey: "replace-repeat");
        var replay = await PutModify(
            http,
            token,
            original.ClOrdId,
            qty: 200,
            price: 30m,
            idempotencyKey: "replace-repeat");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<MutationAck>();
        var replayBody = await replay.Content.ReadFromJsonAsync<MutationAck>();
        Assert.Equal(firstBody!.MutationId, replayBody!.MutationId);
        Assert.Equal(firstBody.ClOrdId, replayBody.ClOrdId);
        Assert.False(firstBody.Replayed);
        Assert.True(replayBody.Replayed);
        var ledger = f.Services.GetRequiredService<OutboundMutationLedger>();
        Assert.True(ledger.TryGet(
            new OutboundMutationId(Guid.Parse(firstBody.MutationId)),
            out var mutation));
        Assert.Single(mutation!.Attempts);
    }

    [Fact]
    public async Task PUT_orders_FreshKeyDuringActiveReplace_BindsToActiveMutation()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var original = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await PutModify(
            http, token, original!.ClOrdId, qty: 200, price: 30m,
            idempotencyKey: "replace-active-first");
        var alias = await PutModify(
            http, token, original.ClOrdId, qty: 200, price: 30m,
            idempotencyKey: "replace-active-alias");
        var firstBody = await first.Content.ReadFromJsonAsync<MutationAck>();
        var aliasBody = await alias.Content.ReadFromJsonAsync<MutationAck>();

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, alias.StatusCode);
        Assert.Equal(firstBody!.MutationId, aliasBody!.MutationId);
        Assert.Equal(firstBody.ClOrdId, aliasBody.ClOrdId);
        TerminalizeMutation(f, firstBody.MutationId);

        var replay = await PutModify(
            http, token, original.ClOrdId, qty: 200, price: 30m,
            idempotencyKey: "replace-active-alias");
        var replayBody = await replay.Content.ReadFromJsonAsync<MutationAck>();

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(firstBody.MutationId, replayBody!.MutationId);
        Assert.Equal(firstBody.ClOrdId, replayBody.ClOrdId);
        Assert.True(replayBody.Replayed);
        var ledger = f.Services.GetRequiredService<OutboundMutationLedger>();
        Assert.True(ledger.TryGet(
            new OutboundMutationId(Guid.Parse(firstBody.MutationId)),
            out var mutation));
        Assert.Single(mutation!.Attempts);
    }

    [Fact]
    public async Task PUT_orders_FreshKeyWithDifferentBodyDuringActiveReplace_Returns409()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var original = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await PutModify(
            http, token, original!.ClOrdId, qty: 200, price: 30m,
            idempotencyKey: "replace-mismatch-first");
        var mismatch = await PutModify(
            http, token, original.ClOrdId, qty: 300, price: 30m,
            idempotencyKey: "replace-mismatch-second");
        var mismatchRetry = await PutModify(
            http, token, original.ClOrdId, qty: 300, price: 30m,
            idempotencyKey: "replace-mismatch-second");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, mismatchRetry.StatusCode);
        var ledger = f.Services.GetRequiredService<OutboundMutationLedger>();
        var mutation = Assert.Single(
            ledger.SnapshotMutations(),
            candidate => candidate.Kind == OutboundMutationKind.Replace);
        Assert.Equal(200, mutation.Approval!.CanonicalCommandNonSensitive.Quantity);
        Assert.Single(mutation.Attempts);
    }

    [Fact]
    public async Task DELETE_orders_IdempotentRepeat_ReplaysMutationWithoutSecondAttempt()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var original = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await DeleteOrder(
            http,
            token,
            original!.ClOrdId,
            idempotencyKey: "cancel-repeat");
        var replay = await DeleteOrder(
            http,
            token,
            original.ClOrdId,
            idempotencyKey: "cancel-repeat");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<MutationAck>();
        var replayBody = await replay.Content.ReadFromJsonAsync<MutationAck>();
        Assert.Equal(firstBody!.MutationId, replayBody!.MutationId);
        Assert.Equal(firstBody.ClOrdId, replayBody.ClOrdId);
        Assert.False(firstBody.Replayed);
        Assert.True(replayBody.Replayed);
        var ledger = f.Services.GetRequiredService<OutboundMutationLedger>();
        Assert.True(ledger.TryGet(
            new OutboundMutationId(Guid.Parse(firstBody.MutationId)),
            out var mutation));
        Assert.Single(mutation!.Attempts);
    }

    [Fact]
    public async Task DELETE_orders_FreshKeyDuringActiveCancel_BindsToActiveMutation()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var original = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await DeleteOrder(
            http, token, original!.ClOrdId, idempotencyKey: "cancel-active-first");
        var alias = await DeleteOrder(
            http, token, original.ClOrdId, idempotencyKey: "cancel-active-alias");
        var firstBody = await first.Content.ReadFromJsonAsync<MutationAck>();
        var aliasBody = await alias.Content.ReadFromJsonAsync<MutationAck>();

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, alias.StatusCode);
        Assert.Equal(firstBody!.MutationId, aliasBody!.MutationId);
        Assert.Equal(firstBody.ClOrdId, aliasBody.ClOrdId);
        TerminalizeMutation(f, firstBody.MutationId);

        var replay = await DeleteOrder(
            http, token, original.ClOrdId, idempotencyKey: "cancel-active-alias");
        var replayBody = await replay.Content.ReadFromJsonAsync<MutationAck>();

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(firstBody.MutationId, replayBody!.MutationId);
        Assert.Equal(firstBody.ClOrdId, replayBody.ClOrdId);
        Assert.True(replayBody.Replayed);
        var ledger = f.Services.GetRequiredService<OutboundMutationLedger>();
        Assert.True(ledger.TryGet(
            new OutboundMutationId(Guid.Parse(firstBody.MutationId)),
            out var mutation));
        Assert.Single(mutation!.Attempts);
    }

    [Fact]
    public async Task PUT_orders_IdempotencyKeyReusedWithDifferentBody_Returns409()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var original = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await PutModify(
            http,
            token,
            original!.ClOrdId,
            qty: 200,
            price: 30m,
            idempotencyKey: "replace-conflict");
        var conflict = await PutModify(
            http,
            token,
            original.ClOrdId,
            qty: 250,
            price: 30m,
            idempotencyKey: "replace-conflict");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostOrder(
        HttpClient http, string token, int qty, decimal price, string side = "Buy")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = side,
                Type = "Limit",
                Quantity = qty,
                Price = price,
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PutModify(
        HttpClient http,
        string token,
        string clOrdId,
        int qty,
        decimal? price,
        string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/orders/{clOrdId}")
        {
            Content = JsonContent.Create(new { Quantity = qty, Price = price })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null)
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> DeleteOrder(
        HttpClient http,
        string token,
        string clOrdId,
        string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/orders/{clOrdId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null)
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await http.SendAsync(req);
    }

    private static void TerminalizeMutation(TestAppFactory factory, string mutationId)
    {
        var ledger = factory.Services.GetRequiredService<OutboundMutationLedger>();
        var id = new OutboundMutationId(Guid.Parse(mutationId));
        Assert.True(ledger.TryGet(id, out var mutation));
        if (mutation!.State is not OutboundMutationState.Ambiguous
            and not OutboundMutationState.ProvenUnsent)
        {
            ledger.ClassifyRecoveredAttempts(
                new ProcessEpochId(Guid.NewGuid()),
                DateTimeOffset.UtcNow);
        }
        ledger.Apply(new OutboundOperatorResolvedEvent
        {
            MutationId = id,
            Decision = OutboundOperatorDecision.VenueAbsent,
            EvidenceType = OutboundOperatorEvidenceType.OfficialExtract,
            EvidenceDigest = new string('a', 64),
            OperatorRef = $"api-test-{mutationId}",
            ResolvedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private sealed record OrderAck(string ClOrdId, string? Status, string? Reason);
    private sealed record ModifyAck(string ClOrdId, string OriginalClOrdId);
    private sealed record MutationAck(
        string MutationId,
        string ClOrdId,
        string State,
        bool Replayed);
}
