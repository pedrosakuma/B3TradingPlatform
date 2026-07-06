using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_MarketData;

/// <summary>
/// Spec — MarketData outage / recovery. A cut marketdata leg must degrade the
/// read-side ref-price freshness without taking down the execution path; once
/// reattached, a fresh trade must resume live ref-price updates.
/// </summary>
[Trait("Category", "Conformance")]
public class MarketDataOutageSpecTests
{
    private const string Symbol = "PETR4";
    private const decimal BaselinePrice = 31.50m;
    private const decimal OutagePrice = 31.60m;
    private const decimal RecoveryPrice = 31.70m;
    private const long CrossQuantity = 100;
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TradeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OutageObservationWindow = TimeSpan.FromSeconds(5);

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task MarketDataNetworkCut_TradingStaysHealthy_LiveCacheStopsThenRecovers()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var docker = new DockerVenueTransportController();

        await WaitForFirmEstablishedAsync(http, adminAuth);

        var baseline = await EstablishBaselineAsync(http, userAuth, adminAuth);

        await using var detached = await docker.DisconnectMarketDataAsync();
        await Task.Delay(PollInterval);

        await AssertTradingHostHealthyAsync(http);

        var outageTradeStartUtc = DateTimeOffset.UtcNow;
        await CrossTradeAndAssertFilledAsync(http, userAuth, Symbol, OutagePrice);
        await AssertTradingHostHealthyAsync(http);

        var duringOutage = await AssertReferencePriceStaysPinnedDuringOutageAsync(
            http,
            adminAuth,
            Symbol,
            expectedPrice: baseline.Live!.Price,
            mustRemainBeforeUtc: outageTradeStartUtc);

        Assert.Equal("Live", duringOutage.EffectiveSource);
        Assert.Equal(baseline.Live.Price, duringOutage.EffectivePrice);
        Assert.NotNull(duringOutage.Live);
        Assert.True(duringOutage.Live!.UpdatedUtc < outageTradeStartUtc,
            $"Expected outage-era live cache sample to predate the outage trade window ({outageTradeStartUtc:o}), observed {duringOutage.Live.UpdatedUtc:o}.");

        await detached.ReconnectAsync();

        var recoveryTradeStartUtc = DateTimeOffset.UtcNow;
        var recovered = await CrossTradeAndWaitForReferencePriceAsync(
            http, userAuth, adminAuth, Symbol, RecoveryPrice, recoveryTradeStartUtc, maxAttempts: 3);

        Assert.Equal("Live", recovered.EffectiveSource);
        Assert.NotNull(recovered.Live);
        Assert.Equal(RecoveryPrice, recovered.Live!.Price);
        Assert.True(recovered.Live.UpdatedUtc > recoveryTradeStartUtc,
            $"Expected recovered live ref-price sample after {recoveryTradeStartUtc:o}, observed {recovered.Live.UpdatedUtc:o}.");

        await docker.WaitForMarketDataTradeDrainAsync(recoveryTradeStartUtc, TradeTimeout);
    }

    private static async Task<ReferencePriceDiagnostic> EstablishBaselineAsync(
        HttpClient http,
        AuthenticationHeaderValue userAuth,
        AuthenticationHeaderValue adminAuth)
    {
        var current = await GetReferencePriceAsync(http, adminAuth, Symbol);
        if (current.EffectiveSource == "Live" &&
            current.Live is not null &&
            current.Live.Price != OutagePrice &&
            current.Live.Price != RecoveryPrice)
        {
            return current;
        }

        var baselineTradeStartUtc = DateTimeOffset.UtcNow;
        return await CrossTradeAndWaitForReferencePriceAsync(
            http, userAuth, adminAuth, Symbol, BaselinePrice, baselineTradeStartUtc, maxAttempts: 2);
    }

    private static async Task<ReferencePriceDiagnostic> CrossTradeAndWaitForReferencePriceAsync(
        HttpClient http,
        AuthenticationHeaderValue userAuth,
        AuthenticationHeaderValue adminAuth,
        string symbol,
        decimal price,
        DateTimeOffset submitStartUtc,
        int maxAttempts = 1)
    {
        ReferencePriceDiagnostic? lastSeen = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var attemptStartUtc = attempt == 1 ? submitStartUtc : DateTimeOffset.UtcNow;
            await CrossTradeAndAssertFilledAsync(http, userAuth, symbol, price);

            var deadline = DateTimeOffset.UtcNow + PollTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var current = await GetReferencePriceAsync(http, adminAuth, symbol);
                lastSeen = current;
                if (current.Live is { } live &&
                    live.Price == price &&
                    live.UpdatedUtc > attemptStartUtc)
                {
                    Assert.Equal("Live", current.EffectiveSource);
                    Assert.Equal(price, current.EffectivePrice);
                    return current;
                }

                await Task.Delay(PollInterval);
            }
        }

        Assert.Fail(
            $"Timed out after {maxAttempts} attempt(s) waiting for {symbol} live ref-price=={price} updated after {submitStartUtc:o}. Last seen: {Format(lastSeen)}.");
        return null!;
    }

    private static async Task CrossTradeAndAssertFilledAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price)
    {
        await CancelOpenOrdersForSymbolAsync(http, auth, symbol);

        var buyClOrdId = await SubmitOrderAndAssertAcceptedAsync(http, auth, symbol, price, side: "Buy");
        await WaitForOrderAsync(http, auth, buyClOrdId, order =>
                order.Status == "Working" && order.CumulativeQuantity == 0,
            TradeTimeout,
            $"{symbol} buy order to reach Working before the cross at {price}");

        var sellClOrdId = await SubmitOrderAndAssertAcceptedAsync(http, auth, symbol, price, side: "Sell");

        var filledBuy = await WaitForOrderAsync(http, auth, buyClOrdId, order =>
                order.Status == "Filled" && order.CumulativeQuantity == CrossQuantity,
            TradeTimeout,
            $"{symbol} buy order to reach Filled at {price}");
        var filledSell = await WaitForOrderAsync(http, auth, sellClOrdId, order =>
                order.Status == "Filled" && order.CumulativeQuantity == CrossQuantity,
            TradeTimeout,
            $"{symbol} sell order to reach Filled at {price}");

        Assert.Equal(CrossQuantity, filledBuy.CumulativeQuantity);
        Assert.Equal(CrossQuantity, filledSell.CumulativeQuantity);
    }

    private static async Task CancelOpenOrdersForSymbolAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol)
    {
        for (var sweep = 0; sweep < 2; sweep++)
        {
            var openOrders = await ListOpenOrdersForSymbolAsync(http, auth, symbol);
            if (openOrders.Count == 0)
                return;

            foreach (var openOrder in openOrders)
            {
                using var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/orders/{openOrder.ClOrdId}");
                cancel.Headers.Authorization = auth;
                var resp = await http.SendAsync(cancel);
                Assert.True(
                    resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                    $"DELETE /orders/{openOrder.ClOrdId} expected 204/404 while clearing {symbol}, got {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
            }

            var deadline = DateTimeOffset.UtcNow + TradeTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if ((await ListOpenOrdersForSymbolAsync(http, auth, symbol)).Count == 0)
                    return;
                await Task.Delay(PollInterval);
            }
        }

        var remaining = await ListOpenOrdersForSymbolAsync(http, auth, symbol);
        Assert.Fail($"Timed out clearing pre-existing open orders for {symbol}. Remaining: {string.Join(", ", remaining.Select(o => $"{o.ClOrdId}:{o.Status}:{o.Symbol}"))}");
    }

    private static async Task<ReferencePriceDiagnostic> AssertReferencePriceStaysPinnedDuringOutageAsync(
        HttpClient http,
        AuthenticationHeaderValue adminAuth,
        string symbol,
        decimal expectedPrice,
        DateTimeOffset mustRemainBeforeUtc)
    {
        var deadline = DateTimeOffset.UtcNow + OutageObservationWindow;
        ReferencePriceDiagnostic? lastSeen = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lastSeen = await GetReferencePriceAsync(http, adminAuth, symbol);

            Assert.Equal("Live", lastSeen.EffectiveSource);
            Assert.NotNull(lastSeen.Live);
            Assert.Equal(expectedPrice, lastSeen.Live!.Price);
            Assert.Equal(expectedPrice, lastSeen.EffectivePrice);
            Assert.True(lastSeen.Live.UpdatedUtc < mustRemainBeforeUtc,
                $"Expected outage-era live cache sample to stay before {mustRemainBeforeUtc:o}, observed {lastSeen.Live.UpdatedUtc:o}.");

            await Task.Delay(PollInterval);
        }

        return lastSeen!;
    }

    private static async Task AssertTradingHostHealthyAsync(HttpClient http)
    {
        var ready = await http.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var health = await http.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("ready", health.GetProperty("status").GetString());
        var exchange = health.GetProperty("exchange");
        Assert.True(exchange.GetProperty("readyForOrders").GetBoolean(),
            $"/health exchange.readyForOrders=false during marketdata outage: {health}");
    }

    private static async Task WaitForFirmEstablishedAsync(
        HttpClient http,
        AuthenticationHeaderValue auth)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        string? lastState = null;
        int? lastFirmCount = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/firms");
            req.Headers.Authorization = auth;
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var firms = json.GetProperty("firms");
                lastFirmCount = firms.GetArrayLength();
                if (lastFirmCount > 0)
                {
                    var state = firms[0].TryGetProperty("sessionState", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString()
                        : null;
                    lastState = state;
                    if (string.Equals(state, "established", StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out waiting for FirmGateway to reach 'established' (last sessionState={lastState ?? "<none>"}, firmCount={lastFirmCount?.ToString() ?? "<none>"}).");
    }

    private static async Task<ulong> SubmitOrderAndAssertAcceptedAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price,
        string side)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new
            {
                symbol,
                side,
                type = "Limit",
                quantity = CrossQuantity,
                price,
            }),
        };

        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Accepted,
            $"{side} POST /orders expected 202 Accepted, got {(int)resp.StatusCode}: {body}");

        var json = JsonDocument.Parse(body).RootElement;
        if (json.TryGetProperty("status", out var statusProp))
        {
            var status = statusProp.GetString();
            Assert.True(!string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase),
                $"{side} POST /orders was risk-rejected before reaching matching: {body}");
        }

        return ulong.Parse(json.GetProperty("clOrdId").GetString()!);
    }

    private static async Task<OrderSnapshot> WaitForOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId,
        Func<OrderSnapshot, bool> predicate,
        TimeSpan timeout,
        string expectation)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        OrderSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await TryGetOrderAsync(http, auth, clOrdId);
            if (last is not null && predicate(last))
                return last;
            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for {expectation} on order {clOrdId}. Last observed={Format(last)}");
        return null!;
    }

    private static async Task<OrderSnapshot?> TryGetOrderAsync(
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

    private static async Task<List<OpenOrderSnapshot>> ListOpenOrdersForSymbolAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/orders");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var orders = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
        if (orders is null)
            return [];

        var result = new List<OpenOrderSnapshot>();
        foreach (var order in orders)
        {
            var orderSymbol = order.GetProperty("symbol").GetString();
            var status = order.GetProperty("status").GetString();
            if (!string.Equals(orderSymbol, symbol, StringComparison.Ordinal) ||
                status is not ("Working" or "PartiallyFilled"))
            {
                continue;
            }

            result.Add(new OpenOrderSnapshot(
                ulong.Parse(order.GetProperty("clOrdId").GetString()!),
                orderSymbol!,
                status!));
        }

        return result;
    }

    private static async Task<ReferencePriceDiagnostic> GetReferencePriceAsync(
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

        LiveBlock? live = null;
        if (entry.TryGetProperty("live", out var liveProp) && liveProp.ValueKind == JsonValueKind.Object)
        {
            live = new LiveBlock(
                liveProp.GetProperty("price").GetDecimal(),
                liveProp.GetProperty("updatedUtc").GetDateTimeOffset());
        }

        return new ReferencePriceDiagnostic(
            Symbol: entry.GetProperty("symbol").GetString()!,
            EffectivePrice: entry.GetProperty("effectivePrice").ValueKind == JsonValueKind.Null
                ? null
                : entry.GetProperty("effectivePrice").GetDecimal(),
            EffectiveSource: entry.GetProperty("effectiveSource").GetString()!,
            Live: live,
            FallbackPrice: entry.GetProperty("fallbackPrice").ValueKind == JsonValueKind.Null
                ? null
                : entry.GetProperty("fallbackPrice").GetDecimal());
    }

    private static string Format(ReferencePriceDiagnostic? d) => d is null
        ? "<none>"
        : $"{{ source={d.EffectiveSource}, effective={d.EffectivePrice?.ToString() ?? "null"}, live={(d.Live is { } l ? $"{l.Price}@{l.UpdatedUtc:o}" : "null")}, fallback={d.FallbackPrice?.ToString() ?? "null"} }}";

    private static string Format(OrderSnapshot? order) => order is null
        ? "<missing>"
        : $"{{ status={order.Status}, cumulativeQuantity={order.CumulativeQuantity}, isStale={order.IsStale}, staleReason={order.StaleReason ?? "null"} }}";

    private sealed record ReferencePriceDiagnostic(
        string Symbol,
        decimal? EffectivePrice,
        string EffectiveSource,
        LiveBlock? Live,
        decimal? FallbackPrice);

    private sealed record LiveBlock(decimal Price, DateTimeOffset UpdatedUtc);

    private sealed record OrderSnapshot(
        string Status,
        long CumulativeQuantity,
        bool IsStale,
        string? StaleReason);

    private sealed record OpenOrderSnapshot(
        ulong ClOrdId,
        string Symbol,
        string Status);
}
