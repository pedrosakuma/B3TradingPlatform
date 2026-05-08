using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #108 — defensive DuplicateClOrdID guard. The
/// <see cref="ClOrdIdPrefixRegistry"/>'s per-end-client counter is
/// allocated atomically, so two concurrent submits never collide on
/// the hot path. The realistic failure mode is a snapshot/WAL-replay
/// regression where the counter watermark falls behind the persisted
/// state at recovery — Restore() then re-allocates IDs already in
/// the book. These tests force that scenario by Restoring the
/// registry to an older snapshot and asserting submit/modify both
/// reject with 409 + reason <c>duplicate_clordid</c> (no WAL append,
/// no gateway dispatch).
/// </summary>
public class DuplicateClOrdIdGuardTests
{
    [Fact]
    public async Task POST_orders_DuplicateClOrdId_Returns409Conflict()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // First submit — establishes the order in the book and
        // advances the registry counter to 1.
        var first = await PostOrder(http, token, qty: 100, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // Snapshot the registry, then roll the per-end-client counter
        // back to 0 so the next Generate() returns the same packed
        // ulong as the order we just submitted.
        var registry = f.Services.GetRequiredService<ClOrdIdPrefixRegistry>();
        var snap = registry.Snapshot();
        Assert.NotEmpty(snap.Counters);
        var rolled = new ClOrdIdRegistrySnapshot
        {
            NextPrefix = snap.NextPrefix,
            Counters = snap.Counters
                .Select(c => new ClOrdIdCounterSnapshot(c.EndClientId, c.PrefixIdx, Counter: 0))
                .ToList(),
        };
        registry.Restore(rolled);

        // Second submit must trip the pre-flight guard.
        var second = await PostOrder(http, token, qty: 50, price: 30m);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.NotNull(body);
        Assert.Equal("duplicate_clordid", body!.Error);
        Assert.False(string.IsNullOrEmpty(body.ClOrdId));
    }

    [Fact]
    public async Task PUT_orders_DuplicateClOrdId_Returns409Conflict()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // Submit order #1 (counter advances to 1).
        var p1 = await PostOrder(http, token, qty: 100, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, p1.StatusCode);
        var ack1 = await p1.Content.ReadFromJsonAsync<OrderAck>();

        // Submit order #2 (counter advances to 2).
        var p2 = await PostOrder(http, token, qty: 200, price: 31m);
        Assert.Equal(HttpStatusCode.Accepted, p2.StatusCode);

        // Roll the counter back to 1 — next Generate() returns the
        // same packed ulong as order #2 (which is in the book).
        var registry = f.Services.GetRequiredService<ClOrdIdPrefixRegistry>();
        var snap = registry.Snapshot();
        var rolled = new ClOrdIdRegistrySnapshot
        {
            NextPrefix = snap.NextPrefix,
            Counters = snap.Counters
                .Select(c => new ClOrdIdCounterSnapshot(c.EndClientId, c.PrefixIdx, Counter: 1))
                .ToList(),
        };
        registry.Restore(rolled);

        // Modify order #1: generates new ClOrdId = counter 2 = order
        // #2's ID. Pre-flight must reject before risk/margin/gateway.
        var put = await PutModify(http, token, ack1!.ClOrdId, qty: 150, price: 30m);
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<ModifyConflictBody>();
        Assert.NotNull(body);
        Assert.Equal("duplicate_clordid", body!.Error);
    }

    private static async Task<HttpResponseMessage> PostOrder(
        HttpClient http, string token, int qty, decimal price)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = qty,
                Price = price,
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PutModify(
        HttpClient http, string token, string clOrdId, int qty, decimal? price)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/orders/{clOrdId}")
        {
            Content = JsonContent.Create(new { Quantity = qty, Price = price })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private sealed record OrderAck(string ClOrdId, string? Status, string? Reason);
    private sealed record ConflictBody(string Error, string ClOrdId);
    private sealed record ModifyConflictBody(string Error, string NewClOrdId);
}
