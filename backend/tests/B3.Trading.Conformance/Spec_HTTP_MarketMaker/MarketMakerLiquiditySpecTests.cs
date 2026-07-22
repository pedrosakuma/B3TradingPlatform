using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_MarketMaker;

/// <summary>
/// Spec — market-maker sandbox liquidity (#683 item 4). An ordinary
/// end-client self-deposits cash via the sandbox-only <c>POST
/// /api/balance/deposit</c> endpoint, then crosses the market-maker bot's
/// resting quotes: buy into the ask, buy again to prove the bot
/// re-quoted a fresh ask after the fill, then sell into the (still
/// resting) bid. Guards the whole point of the overlay
/// (docker-compose.market-maker.yml): a freshly self-funded end-client can
/// actually trade against a bot that keeps reacting to its own fills.
/// </summary>
/// <remarks>
/// Gated on <see cref="ConformanceFactAttribute.RequiresMarketMakerSandbox"/>
/// rather than <c>RequiresSandboxMatching</c> deliberately: the bot rests a
/// tight bid/ask around each instrument's configured reference price
/// (PETR4 29.95/30.05, see docker/docker-compose.market-maker.yml), which
/// would otherwise intercept the same-user Buy+Sell pairs the
/// <c>RequiresSandboxMatching</c> specs (e.g. <c>MarketDataOutageSpecTests</c>)
/// submit to observe a self-print — those specs assume their own Buy fills
/// their own Sell 1:1, which the venue can't guarantee once a better-priced
/// third-party quote is resting in the book. CI runs this scenario against
/// its own isolated stack/job (market-maker overlay stacked, but without
/// B3T_REAL_STACK_CONFORMANCE) so the two profiles never share an order book.
/// </remarks>
[Trait("Category", "Conformance")]
public class MarketMakerLiquiditySpecTests
{
    private const string Symbol = "PETR4";
    private const long CrossQuantity = 100; // must match MarketMaker__Instruments__0__QuoteLots * LotSize

    // docker-compose.market-maker-conformance.yml zeroes alice's
    // conformance.yml cash seed for this job, so this deposit is what
    // actually funds the crosses below — otherwise the self-deposit
    // assertion would be meaningless (the trade would succeed even if
    // deposit and margin were completely disconnected). The two
    // sequential buy legs each risk-check at up to ~3,290 BRL notional
    // (MarketableBuyPrice * CrossQuantity, the pessimistic pre-trade
    // margin check uses the limit price, not the better fill price), so
    // size the deposit to comfortably cover both without relying on the
    // first leg's fill freeing exactly enough margin for the second.
    private const decimal DepositAmount = 10_000.00m;

    // PETR4 RefPrice=30.00, SpreadTicks=5, TickSize=0.01 (docker-compose.market-maker.yml)
    // => bot rests Buy@29.95 / Sell@30.05. These marketable limits sit
    // comfortably inside the default 10% PriceCollarPercent band anchored
    // on either the static risk fallback (32.50) or the bot's own 30.00,
    // while still crossing the bot's resting quote on either side.
    private const decimal MarketableBuyPrice = 32.90m;
    private const decimal MarketableSellPrice = 29.30m;

    private static readonly TimeSpan FillTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [ConformanceFact(RequiresMarketMakerSandbox = true)]
    public async Task EndClient_SelfDepositsThenCrossesBotLiquidity_OnBothSides()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var auth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);

        await CancelOpenOrdersForSymbolAsync(http, auth, Symbol);

        var balanceBefore = await GetAvailableBalanceAsync(http, auth);
        var deposit = await SelfDepositAsync(http, auth, DepositAmount);
        Assert.Equal(DepositAmount, deposit.Amount);
        Assert.Equal(balanceBefore + DepositAmount, deposit.Available);

        // Leg 1: buy into the bot's resting ask (30.05). No other
        // counterparty exists in this stack — if this fills, it filled
        // against the bot.
        var buyClOrdId = await SubmitOrderAndAssertAcceptedAsync(http, auth, Symbol, MarketableBuyPrice, side: "Buy");
        await WaitForOrderAsync(http, auth, buyClOrdId,
            order => order.Status == "Filled" && order.CumulativeQuantity == CrossQuantity,
            FillTimeout,
            $"{Symbol} buy order to fill against the market-maker bot's resting ask");

        // Leg 2: buy again at the same marketable price. The original ask
        // was fully consumed by leg 1, so this only fills if the bot
        // re-quoted a fresh ask after the fill (event-driven re-quote, see
        // MarketMakerWorker) — proving the bot reacts to the book instead
        // of resting a one-shot quote. Crossing the SAME side twice is the
        // point: the untouched original bid (still resting from before
        // leg 1) would let a Sell fill here even if re-quoting were
        // completely broken, so a Sell wouldn't prove anything.
        var buyClOrdId2 = await SubmitOrderAndAssertAcceptedAsync(http, auth, Symbol, MarketableBuyPrice, side: "Buy");
        await WaitForOrderAsync(http, auth, buyClOrdId2,
            order => order.Status == "Filled" && order.CumulativeQuantity == CrossQuantity,
            FillTimeout,
            $"{Symbol} second buy order to fill against the market-maker bot's re-quoted ask");

        // Leg 3: sell back into the bid side, confirming the bot quotes
        // two-sided (not ask-only) liquidity.
        var sellClOrdId = await SubmitOrderAndAssertAcceptedAsync(http, auth, Symbol, MarketableSellPrice, side: "Sell");
        await WaitForOrderAsync(http, auth, sellClOrdId,
            order => order.Status == "Filled" && order.CumulativeQuantity == CrossQuantity,
            FillTimeout,
            $"{Symbol} sell order to fill against the market-maker bot's re-quoted bid");
    }

    private static async Task<decimal> GetAvailableBalanceAsync(HttpClient http, AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/balance");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("available").GetDecimal();
    }

    private static async Task<SelfDepositResult> SelfDepositAsync(HttpClient http, AuthenticationHeaderValue auth, decimal amount)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/balance/deposit")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new { amount }),
        };

        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK,
            $"POST /api/balance/deposit expected 200 OK (is Trading__Sandbox__AllowSelfCashDeposit=true set on the target?), got {(int)resp.StatusCode}: {body}");

        var json = JsonDocument.Parse(body).RootElement;
        return new SelfDepositResult(json.GetProperty("amount").GetDecimal(), json.GetProperty("available").GetDecimal());
    }

    private static async Task CancelOpenOrdersForSymbolAsync(HttpClient http, AuthenticationHeaderValue auth, string symbol)
    {
        for (var sweep = 0; sweep < 2; sweep++)
        {
            var openOrders = await ListOpenOrdersForSymbolAsync(http, auth, symbol);
            if (openOrders.Count == 0)
                return;

            foreach (var clOrdId in openOrders)
            {
                using var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/api/orders/{clOrdId}");
                cancel.Headers.Authorization = auth;
                var resp = await http.SendAsync(cancel);
                Assert.True(
                    resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.Conflict,
                    $"DELETE /api/orders/{clOrdId} expected 204/404/409 while clearing {symbol}, got {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
            }

            var deadline = DateTimeOffset.UtcNow + FillTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if ((await ListOpenOrdersForSymbolAsync(http, auth, symbol)).Count == 0)
                    return;
                await Task.Delay(PollInterval);
            }
        }

        var remaining = await ListOpenOrdersForSymbolAsync(http, auth, symbol);
        Assert.Fail($"Timed out clearing pre-existing open orders for {symbol}. Remaining clOrdIds: {string.Join(", ", remaining)}");
    }

    private static async Task<List<ulong>> ListOpenOrdersForSymbolAsync(HttpClient http, AuthenticationHeaderValue auth, string symbol)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var orders = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
        if (orders is null)
            return [];

        var result = new List<ulong>();
        foreach (var order in orders)
        {
            var orderSymbol = order.GetProperty("symbol").GetString();
            var status = order.GetProperty("status").GetString();
            if (string.Equals(orderSymbol, symbol, StringComparison.Ordinal) && status is "Working" or "PartiallyFilled")
                result.Add(ulong.Parse(order.GetProperty("clOrdId").GetString()!));
        }

        return result;
    }

    private static async Task<ulong> SubmitOrderAndAssertAcceptedAsync(
        HttpClient http, AuthenticationHeaderValue auth, string symbol, decimal price, string side)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
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
            $"{side} POST /api/orders expected 202 Accepted, got {(int)resp.StatusCode}: {body}");

        var json = JsonDocument.Parse(body).RootElement;
        if (json.TryGetProperty("status", out var statusProp))
        {
            var status = statusProp.GetString();
            Assert.True(!string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase),
                $"{side} POST /api/orders was risk-rejected before reaching matching: {body}");
        }

        return ulong.Parse(json.GetProperty("clOrdId").GetString()!);
    }

    private static async Task<OrderSnapshot> WaitForOrderAsync(
        HttpClient http, AuthenticationHeaderValue auth, ulong clOrdId,
        Func<OrderSnapshot, bool> predicate, TimeSpan timeout, string expectation)
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

    private static async Task<OrderSnapshot?> TryGetOrderAsync(HttpClient http, AuthenticationHeaderValue auth, ulong clOrdId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
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
                    order.GetProperty("status").GetString()!,
                    order.GetProperty("cumulativeQuantity").GetInt64());
            }
        }

        return null;
    }

    private static string Format(OrderSnapshot? order) => order is null
        ? "<missing>"
        : $"{{ status={order.Status}, cumulativeQuantity={order.CumulativeQuantity} }}";

    private sealed record SelfDepositResult(decimal Amount, decimal Available);

    private sealed record OrderSnapshot(string Status, long CumulativeQuantity);
}
