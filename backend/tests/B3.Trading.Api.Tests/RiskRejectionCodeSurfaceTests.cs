using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #288 — REST surface emits a stable machine-readable <c>code</c>
/// (e.g. <c>min_tick_size</c>) alongside the human-readable
/// <c>reason</c>/<c>error</c>, on both <c>POST /orders</c>
/// (<c>Status="Rejected"</c>, 202) and <c>PUT /orders/{id}</c>
/// (<c>422 UnprocessableEntity</c>) risk-rejection paths.
///
/// <para>
/// The wire field is the lower_snake_case <see cref="IRiskCheck.Name"/>
/// of the rejecting check (the pipeline fall-back), with explicit
/// canonical values listed in <c>RiskRejectCodes</c>. Tests target the
/// tick-size check because it's the smallest unambiguous trigger.
/// </para>
/// </summary>
public class RiskRejectionCodeSurfaceTests
{
    [Fact]
    public async Task POST_TickSizeViolation_Surfaces_MinTickSize_Code()
    {
        // Seed PETR4 with a 0.01 tick; submit a price that is not a
        // whole multiple (0.005) to trigger MinTickSizeCheck.
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:SymbolDirectory:Specs:PETR4:TickSize"] = "0.01",
        });
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var resp = await PostOrder(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            TimeInForce = "Day",
            Quantity = 100,
            Price = 30.005m,
        });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var ack = await ReadAck(resp);
        Assert.Equal("Rejected", ack.Status);
        Assert.Equal("min_tick_size", ack.Code);
        // Reason still carries the existing human-readable detail.
        Assert.Contains("tick size", ack.Reason);
    }

    [Fact]
    public async Task PUT_ModifyToOffTick_Surfaces_MinTickSize_Code_AsUnprocessable()
    {
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:SymbolDirectory:Specs:PETR4:TickSize"] = "0.01",
        });
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // Submit an on-tick order first.
        var posted = await PostOrder(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            TimeInForce = "Day",
            Quantity = 100,
            Price = 29.90m,
        });
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var origAck = await ReadAck(posted);
        Assert.Null(origAck.Status); // Accepted (not "Rejected")

        // Modify to an off-tick price (0.005 increment).
        var put = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origAck.ClOrdId}")
        {
            Content = JsonContent.Create(new
            {
                Quantity = 100,
                Price = 29.905m,
            }),
        };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(put);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var err = JsonSerializer.Deserialize<ErrCode>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(err);
        Assert.Equal("min_tick_size", err!.Code);
        Assert.Contains("tick size", err.Error);
    }

    [Fact]
    public async Task PUT_RejectedModify_WithSameIdempotencyKey_ReplaysWithoutBurningAnotherClOrdId()
    {
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:SymbolDirectory:Specs:PETR4:TickSize"] = "0.01",
        });
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            TimeInForce = "Day",
            Quantity = 100,
            Price = 29.90m,
        });
        var origAck = await ReadAck(posted);
        const string idempotencyKey = "rejected-modify-replay";

        async Task<HttpResponseMessage> SendModify()
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origAck.ClOrdId}")
            {
                Content = JsonContent.Create(new
                {
                    Quantity = 100,
                    Price = 29.905m,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            return await http.SendAsync(request);
        }

        var first = await SendModify();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var counterAfterFirst = f.Services.GetRequiredService<ClOrdIdPrefixRegistry>()
            .Snapshot().Counters.Single(x => x.EndClientId == TestAppFactory.TestUser).Counter;

        var replay = await SendModify();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());
        var counterAfterReplay = f.Services.GetRequiredService<ClOrdIdPrefixRegistry>()
            .Snapshot().Counters.Single(x => x.EndClientId == TestAppFactory.TestUser).Counter;
        Assert.Equal(counterAfterFirst, counterAfterReplay);

        var binding = Assert.Single(f.Services.GetRequiredService<RestOrderIdempotencyStore>()
            .CaptureSnapshot());
        Assert.Equal("min_tick_size", binding.RejectionCode);
        Assert.Contains("tick size", binding.RejectionReason);
    }

    private static async Task<HttpResponseMessage> PostOrder(HttpClient http, string token, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static async Task<OrderAck> ReadAck(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        var ack = JsonSerializer.Deserialize<OrderAck>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(ack);
        return ack!;
    }

    private sealed record OrderAck(string ClOrdId, string? Status, string? Reason, string? Code);
    private sealed record ErrCode(string? Error, string? Code);
}
