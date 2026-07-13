using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

internal static class SessionRollSpecSupport
{
    internal const string FirmId = "FIRM01";
    internal const long RoundTripQuantity = 100;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan TradeTimeout = TimeSpan.FromSeconds(30);

    internal static decimal PriceNearLowerCollar(decimal referencePrice)
        => decimal.Round(referencePrice * 0.92m, 2, MidpointRounding.AwayFromZero);

    internal static decimal PriceNearUpperCollar(decimal referencePrice)
        => decimal.Round(referencePrice * 1.08m, 2, MidpointRounding.AwayFromZero);

    internal static async Task<ulong> SubmitOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price,
        string side = "Buy",
        long quantity = RoundTripQuantity)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new
            {
                symbol,
                side,
                type = "Limit",
                quantity,
                price,
            }),
        };

        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Accepted,
            $"POST /orders expected 202 Accepted, got {(int)resp.StatusCode}: {body}");

        var json = JsonDocument.Parse(body).RootElement;
        if (json.TryGetProperty("status", out var statusProp))
        {
            Assert.NotEqual("Rejected", statusProp.GetString());
        }

        return ulong.Parse(json.GetProperty("clOrdId").GetString()!);
    }

    internal static async Task AssertPostRecoveryTradingRoundTripAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        DockerVenueTransportController docker,
        string symbol,
        decimal price,
        long quantity)
    {
        var submitStartUtc = DateTimeOffset.UtcNow;
        var buyClOrdId = await SubmitOrderAsync(http, auth, symbol, price, side: "Buy", quantity: quantity);
        await WaitForOrderAsync(http, auth, buyClOrdId, order =>
                (order.Status == "Working" && order.CumulativeQuantity == 0) ||
                (order.Status == "Filled" && order.CumulativeQuantity == quantity),
            OrderTimeout,
            "post-recovery buy order to reach Working (or immediately Filled against a surviving opposite book)");

        var sellClOrdId = await SubmitOrderAsync(http, auth, symbol, price, side: "Sell", quantity: quantity);

        // GET /orders is the full per-client history projection, not an
        // "open orders only" book view. Contract-level "disappears from the
        // book" therefore means the order leaves Working and reaches a
        // terminal state; it should remain queryable here as Filled.
        var filledBuy = await WaitForOrderAsync(http, auth, buyClOrdId, order =>
                order.Status == "Filled" && order.CumulativeQuantity == quantity,
            TradeTimeout,
            "post-recovery buy order to reach Filled");
        var filledSell = await WaitForOrderAsync(http, auth, sellClOrdId, order =>
                order.Status == "Filled" && order.CumulativeQuantity == quantity,
            TradeTimeout,
            "post-recovery sell order to reach Filled");

        Assert.Equal(quantity, filledBuy.CumulativeQuantity);
        Assert.Equal(quantity, filledSell.CumulativeQuantity);

        // The FIXP/order path can recover slightly ahead of the separate
        // UMDF channel-84 stream after a forced venue fault. Wait until
        // marketdata's own progress logs show the post-recovery trade window
        // drained without the reconnect-era stale gate still being on before
        // handing off to the next real-stack spec.
        await docker.WaitForMarketDataTradeDrainAsync(submitStartUtc, TradeTimeout);
    }

    internal static async Task<ulong?> StimulateGatewayWriteAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price,
        string side = "Buy")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new
            {
                symbol,
                side,
                type = "Limit",
                quantity = 100,
                price,
            }),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using var resp = await http.SendAsync(req, cts.Token);
            if (resp.StatusCode != HttpStatusCode.Accepted)
                return null;

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            if (body.TryGetProperty("clOrdId", out var clOrdIdProp) &&
                ulong.TryParse(clOrdIdProp.GetString(), out var clOrdId))
            {
                return clOrdId;
            }
        }
        catch (OperationCanceledException)
        {
            // Intentional: the point is to force the host to attempt a FIXP
            // write while the venue leg is severed, not to assert on the
            // HTTP outcome of this probe order.
        }
        catch (HttpRequestException)
        {
        }

        return null;
    }

    internal static async Task CancelOrderIfPresentAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/orders/{clOrdId}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NotFound ||
            resp.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        Assert.True(resp.StatusCode == HttpStatusCode.NoContent,
            $"DELETE /orders/{clOrdId} expected 204/404/409, got {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    internal static async Task<OrderSnapshot> WaitForOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId,
        Func<OrderSnapshot, bool> predicate,
        TimeSpan timeout,
        string expectation)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        OrderSnapshot? last = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await TryGetOrderAsync(http, auth, clOrdId);
                lastError = null;
                if (last is not null && predicate(last))
                    return last;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for {expectation} on order {clOrdId}. Last observed={Format(last)} httpError={lastError ?? "<none>"}");
        return null!;
    }

    internal static async Task<OrderSnapshot?> TryGetOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/orders");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var orders = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
        if (orders is null)
            return null;

        foreach (var order in orders)
        {
            if (order.GetProperty("clOrdId").GetString() == clOrdId.ToString())
            {
                return new OrderSnapshot(
                    Status: order.GetProperty("status").GetString()!,
                    CumulativeQuantity: order.GetProperty("cumulativeQuantity").GetInt64(),
                    IsStale: order.TryGetProperty("isStale", out var staleProp) && staleProp.GetBoolean(),
                    StaleReason: order.TryGetProperty("staleReason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String
                        ? reasonProp.GetString()
                        : null);
            }
        }

        return null;
    }

    internal static async Task<FirmSnapshot> WaitForFirmEstablishedAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        uint? priorVerId = null,
        bool? expectAdvance = null)
    {
        var deadline = DateTimeOffset.UtcNow + ReconnectTimeout;
        FirmSnapshot? last = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await GetFirmSnapshotAsync(http, auth);
                lastError = null;
                var established = string.Equals(last.SessionState, "established", StringComparison.OrdinalIgnoreCase)
                                  && !last.Reconnecting;
                var verIdOkay = expectAdvance switch
                {
                    true => priorVerId.HasValue && last.SessionVerId > priorVerId.Value,
                    false => priorVerId.HasValue && last.SessionVerId == priorVerId.Value,
                    null => true,
                };

                if (established && verIdOkay)
                    return last;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {ReconnectTimeout.TotalSeconds:F0}s waiting for {FirmId} sessionState=established and sessionVerId expectation {DescribeVerIdExpectation(priorVerId, expectAdvance)}. Last observed={Format(last)} httpError={lastError ?? "<none>"}");
        return null!;
    }

    internal static async Task<FirmSnapshot> GetFirmSnapshotAsync(
        HttpClient http,
        AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/firms");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var firms = json.GetProperty("firms");
        foreach (var firm in firms.EnumerateArray())
        {
            if (firm.GetProperty("firmId").GetString() == FirmId)
            {
                return new FirmSnapshot(
                    SessionState: firm.TryGetProperty("sessionState", out var stateProp) && stateProp.ValueKind == JsonValueKind.String
                        ? stateProp.GetString()
                        : null,
                    SessionVerId: GetUInt32Flexible(firm.GetProperty("sessionVerId")),
                    Reconnecting: firm.TryGetProperty("reconnecting", out var reconnectingProp)
                                  && reconnectingProp.ValueKind == JsonValueKind.True);
            }
        }

        Assert.Fail($"Firm '{FirmId}' not found in /admin/firms response.");
        return null!;
    }

    internal static async Task<decimal> GetEffectiveReferencePriceAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/admin/marketdata/reference-prices?symbols={Uri.EscapeDataString(symbol)}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entry = json.GetProperty("symbols")[0];

        if (entry.TryGetProperty("effectivePrice", out var effectiveProp) &&
            effectiveProp.ValueKind == JsonValueKind.Number)
        {
            return effectiveProp.GetDecimal();
        }

        if (entry.TryGetProperty("fallbackPrice", out var fallbackProp) &&
            fallbackProp.ValueKind == JsonValueKind.Number)
        {
            return fallbackProp.GetDecimal();
        }

        Assert.Fail($"No effective/fallback reference price available for {symbol}.");
        return 0m;
    }

    internal static async Task DelayUntilAsync(DateTimeOffset startedUtc, TimeSpan targetDuration)
    {
        var remaining = startedUtc + targetDuration - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
    }

    private static uint GetUInt32Flexible(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetUInt32(),
        JsonValueKind.String => uint.Parse(value.GetString()!),
        _ => throw new InvalidOperationException($"Expected uint-compatible sessionVerId, observed {value.ValueKind}."),
    };

    private static string DescribeVerIdExpectation(uint? priorVerId, bool? expectAdvance) => expectAdvance switch
    {
        true => $">{priorVerId}",
        false => $"=={priorVerId}",
        null => "<any>",
    };

    private static string Format(OrderSnapshot? order) => order is null
        ? "<missing>"
        : $"{{ status={order.Status}, cumulativeQuantity={order.CumulativeQuantity}, isStale={order.IsStale}, staleReason={order.StaleReason ?? "null"} }}";

    private static string Format(FirmSnapshot? firm) => firm is null
        ? "<missing>"
        : $"{{ sessionState={firm.SessionState ?? "null"}, sessionVerId={firm.SessionVerId}, reconnecting={firm.Reconnecting} }}";


    internal sealed record OrderSnapshot(
        string Status,
        long CumulativeQuantity,
        bool IsStale,
        string? StaleReason);

    internal sealed record FirmSnapshot(
        string? SessionState,
        uint SessionVerId,
        bool Reconnecting);

}
