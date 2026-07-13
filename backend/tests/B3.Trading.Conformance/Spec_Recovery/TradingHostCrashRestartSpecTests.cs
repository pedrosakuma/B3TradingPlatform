using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;
using B3.Trading.Conformance.Spec_FIXP_SessionRoll;

namespace B3.Trading.Conformance.Spec_Recovery;

[Trait("Category", "Conformance")]
public class TradingHostCrashRestartSpecTests
{
    private const string Firm01 = "FIRM01";
    private const string Firm02 = "FIRM02";
    private const string Firm01User = "bob";
    private const string Firm02User = "bob-firm02";
    private const string RestingSymbol = "ITUB4";
    private const string RecoveredStateSymbol = "VALE3";
    private const string OutageFillSymbol = "PETR4";
    private const ulong OutageFillSecurityId = 900000000001UL;
    private const string PostRestartTradeSymbol = "PETR4";
    private const string DirectCounterpartyEndpoint = "matching-platform:9876";
    private const uint DirectCounterpartySessionId = 10102;
    private const uint DirectCounterpartySessionVerId = 1;
    private const uint DirectCounterpartyEnteringFirm = 100;
    private const string DirectCounterpartyAccessKey =
        "{\"auth_type\":\"basic\",\"username\":\"10102\",\"access_key\":\"dev-key-2\"}";
    private const string DirectCounterpartySenderLocation = "SP";
    private const string DirectCounterpartyEnteringTrader = "BOT";
    private const long OrderQuantity = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GatewayTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TradeTimeout = TimeSpan.FromSeconds(30);

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task SigKillRestart_ReplaysStateAndResumesTrading()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var firm01Auth = await LoginWithRetryAsync(http, Firm01User, peer.Password);
        var firm02Auth = await LoginWithRetryAsync(http, Firm02User, peer.Password);
        var adminAuth = await LoginWithRetryAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var docker = new DockerVenueTransportController();

        await WaitForReadyAsync(http);
        _ = await WaitForFirmEstablishedAsync(http, adminAuth, Firm01);
        _ = await WaitForFirmEstablishedAsync(http, adminAuth, Firm02);

        var restingPrice = SessionRollSpecSupport.PriceNearLowerCollar(
            await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, RestingSymbol));
        var recoveredStatePrice = SessionRollSpecSupport.PriceNearLowerCollar(
            await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, RecoveredStateSymbol));
        var buyerBaseline = await GetTradeStateAsync(http, firm02Auth, RecoveredStateSymbol);

        var restingClOrdId = await SubmitOrderAsync(http, firm02Auth, RestingSymbol, restingPrice, side: "Buy");
        var restingBeforeCrash = await WaitForOrderAsync(
            http,
            firm02Auth,
            restingClOrdId,
            order => order.Status == "Working"
                     && order.CumulativeQuantity == 0
                     && order.LeavesQuantity == OrderQuantity
                     && !order.IsStale,
            OrderTimeout,
            "resting order to reach Working before the crash");

        var buyBeforeCrash = await SubmitOrderAsync(http, firm02Auth, RecoveredStateSymbol, recoveredStatePrice, side: "Buy");
        await WaitForOrderAsync(
            http,
            firm02Auth,
            buyBeforeCrash,
            order => order.Status == "Working" && order.CumulativeQuantity == 0,
            OrderTimeout,
            "pre-crash buy order to reach Working before the cross");

        var sellBeforeCrash = await SubmitOrderAsync(http, firm01Auth, RecoveredStateSymbol, recoveredStatePrice, side: "Sell");
        await WaitForOrderAsync(
            http,
            firm02Auth,
            buyBeforeCrash,
            order => order.Status == "Filled" && order.CumulativeQuantity == OrderQuantity,
            TradeTimeout,
            "pre-crash buy order to reach Filled");
        await WaitForOrderAsync(
            http,
            firm01Auth,
            sellBeforeCrash,
            order => order.Status == "Filled" && order.CumulativeQuantity == OrderQuantity,
            TradeTimeout,
            "pre-crash sell order to reach Filled");

        var buyerBeforeCrash = await WaitForTradeStateAsync(
            http,
            firm02Auth,
            RecoveredStateSymbol,
            state => state.PositionNetQuantity == buyerBaseline.PositionNetQuantity + OrderQuantity
                     && state.PositionAverageEntryPrice == recoveredStatePrice
                     && state.RealizedPnl == buyerBaseline.RealizedPnl
                     && state.TotalRealizedPnl == buyerBaseline.TotalRealizedPnl
                     && state.AvailableBalance < buyerBaseline.AvailableBalance,
            "buyer cash/position/pnl to reflect the pre-crash fill");

        var crashStartedUtc = DateTimeOffset.UtcNow;
        await docker.KillTradingHostAsync();
        await docker.WaitForTradingHostNotRunningAsync(TimeSpan.FromSeconds(10));
        await docker.StartTradingHostAsync();
        await docker.WaitForTradingHostRestartAsync(crashStartedUtc, ReadyTimeout);
        await WaitForReadyAsync(http);
        _ = await WaitForFirmEstablishedAsync(http, adminAuth, Firm01);
        _ = await WaitForFirmEstablishedAsync(http, adminAuth, Firm02);
        var restingAfterRestart = await WaitForOrderAsync(
            http,
            firm02Auth,
            restingClOrdId,
            order => order.Status == "Working"
                     && order.CumulativeQuantity == restingBeforeCrash.CumulativeQuantity
                     && order.LeavesQuantity == restingBeforeCrash.LeavesQuantity,
            OrderTimeout,
            "resting order to survive restart with the same working state");

        Assert.Equal(restingBeforeCrash.Status, restingAfterRestart.Status);
        Assert.Equal(restingBeforeCrash.CumulativeQuantity, restingAfterRestart.CumulativeQuantity);
        Assert.Equal(restingBeforeCrash.LeavesQuantity, restingAfterRestart.LeavesQuantity);
        if (restingAfterRestart.IsStale)
            Assert.StartsWith("session_rolled:", restingAfterRestart.StaleReason, StringComparison.Ordinal);

        var buyerAfterRestart = await WaitForTradeStateAsync(
            http,
            firm02Auth,
            buyerBeforeCrash,
            RecoveredStateSymbol,
            "buyer cash/position/pnl to survive the restart unchanged");
        Assert.Equal(buyerBeforeCrash, buyerAfterRestart);

        var postRestartTradePrice = SessionRollSpecSupport.PriceNearUpperCollar(
            await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, PostRestartTradeSymbol));
        await AssertPostRecoveryTradingRoundTripAsync(
            http,
            firm02Auth,
            firm01Auth,
            docker,
            PostRestartTradeSymbol,
            postRestartTradePrice,
            OrderQuantity);

        await TryCleanupRecoveredOrderAsync(http, adminAuth, firm02Auth, Firm02, restingClOrdId, restingAfterRestart.IsStale);
    }

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task SigKillRestart_FillDuringOutage_ReplaysMissedExecutionReport()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var firm02Auth = await LoginWithRetryAsync(http, Firm02User, peer.Password);
        var adminAuth = await LoginWithRetryAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var docker = new DockerVenueTransportController();
        await using var counterparty = await DirectFixpCounterpartyClient.ConnectAsync(
            DirectCounterpartyEndpoint,
            DirectCounterpartySessionId,
            DirectCounterpartySessionVerId,
            DirectCounterpartyEnteringFirm,
            DirectCounterpartyAccessKey,
            DirectCounterpartySenderLocation,
            DirectCounterpartyEnteringTrader);

        await WaitForReadyAsync(http);
        _ = await WaitForFirmEstablishedAsync(http, adminAuth, Firm02);

        var outageFillPrice = SessionRollSpecSupport.PriceNearLowerCollar(
            await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, OutageFillSymbol));
        var buyerBaseline = await GetTradeStateAsync(http, firm02Auth, OutageFillSymbol);

        var restingBuyClOrdId = await SubmitOrderAsync(http, firm02Auth, OutageFillSymbol, outageFillPrice, side: "Buy");
        await WaitForOrderAsync(
            http,
            firm02Auth,
            restingBuyClOrdId,
            order => order.Status == "Working"
                     && order.CumulativeQuantity == 0
                     && order.LeavesQuantity == OrderQuantity
                     && !order.IsStale,
            OrderTimeout,
            "pre-crash resting buy order to reach Working before host kill");

        var crashStartedUtc = DateTimeOffset.UtcNow;
        await docker.KillTradingHostAsync();
        await docker.WaitForTradingHostNotRunningAsync(TimeSpan.FromSeconds(10));

        var counterpartySellClOrdId = await counterparty.SubmitLimitAsync(
            OutageFillSecurityId,
            isBuy: false,
            price: outageFillPrice,
            quantity: OrderQuantity);
        var counterpartyFill = await counterparty.WaitForFilledAsync(counterpartySellClOrdId, TradeTimeout);

        Assert.Equal(OrderQuantity, counterpartyFill.CumulativeQuantity);
        Assert.Equal(0, counterpartyFill.LeavesQuantity);

        await docker.StartTradingHostAsync();
        await docker.WaitForTradingHostRestartAsync(crashStartedUtc, ReadyTimeout);
        await WaitForReadyAsync(http);
        _ = await WaitForFirmEstablishedAsync(http, adminAuth, Firm02);

        var recoveredOrder = await WaitForOrderAsync(
            http,
            firm02Auth,
            restingBuyClOrdId,
            order => order.Status == "Filled"
                     && order.CumulativeQuantity == OrderQuantity
                     && order.LeavesQuantity == 0,
            TradeTimeout,
            "pre-crash resting order to reconcile the fill that happened during the outage");

        Assert.Equal("Filled", recoveredOrder.Status);
        Assert.Equal(OrderQuantity, recoveredOrder.CumulativeQuantity);
        Assert.Equal(0, recoveredOrder.LeavesQuantity);
        Assert.False(recoveredOrder.IsStale);

        var expectedAverageEntryPrice = CalculateExpectedAverageEntryPrice(
            buyerBaseline.PositionNetQuantity,
            buyerBaseline.PositionAverageEntryPrice,
            OrderQuantity,
            outageFillPrice);
        var buyerAfterRestart = await WaitForTradeStateAsync(
            http,
            firm02Auth,
            OutageFillSymbol,
            state => state.PositionNetQuantity == buyerBaseline.PositionNetQuantity + OrderQuantity
                     && state.PositionAverageEntryPrice == expectedAverageEntryPrice
                     && state.RealizedPnl == buyerBaseline.RealizedPnl
                     && state.TotalRealizedPnl == buyerBaseline.TotalRealizedPnl
                     && state.AvailableBalance < buyerBaseline.AvailableBalance,
            "buyer state to reflect the fill that matching executed while trading-host was down");

        Assert.Equal(buyerBaseline.PositionNetQuantity + OrderQuantity, buyerAfterRestart.PositionNetQuantity);
        Assert.Equal(expectedAverageEntryPrice, buyerAfterRestart.PositionAverageEntryPrice);
    }

    private static async Task<AuthenticationHeaderValue> LoginWithRetryAsync(
        HttpClient http,
        string username,
        string password)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        HttpRequestException? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await LoginHelper.LoginAsync(http, username, password);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                await Task.Delay(PollInterval);
            }
        }

        throw new HttpRequestException(
            $"Timed out retrying login for '{username}' against {http.BaseAddress}.",
            last);
    }

    private static async Task WaitForReadyAsync(HttpClient http)
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        HttpStatusCode? lastStatus = null;
        string? lastBody = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var resp = await http.GetAsync("/ready");
                lastStatus = resp.StatusCode;
                lastBody = await resp.Content.ReadAsStringAsync();
                lastError = null;
                if (resp.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException ex)
            {
                lastStatus = null;
                lastBody = null;
                lastError = ex.Message;
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {ReadyTimeout.TotalSeconds:F0}s waiting for /ready to return 200. Last status={(int?)lastStatus} body={lastBody ?? "<none>"} error={lastError ?? "<none>"}.");
    }

    private static async Task<FirmSnapshot> WaitForFirmEstablishedAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string firmId)
    {
        var deadline = DateTimeOffset.UtcNow + GatewayTimeout;
        FirmSnapshot? last = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await GetFirmSnapshotAsync(http, auth, firmId);
                lastError = null;
                if (string.Equals(last.SessionState, "established", StringComparison.OrdinalIgnoreCase) && !last.Reconnecting)
                    return last;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
            {
                lastError = ex.Message;
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {GatewayTimeout.TotalSeconds:F0}s waiting for {firmId} sessionState=established. Last observed={Format(last)} error={lastError ?? "<none>"}.");
        return null!;
    }

    private static async Task<ulong> SubmitOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price,
        string side,
        long quantity = OrderQuantity)
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
            Assert.NotEqual("Rejected", statusProp.GetString());

        return ulong.Parse(json.GetProperty("clOrdId").GetString()!);
    }

    private static async Task ClearStaleAsync(
        HttpClient http,
        AuthenticationHeaderValue adminAuth,
        string firmId,
        ulong clOrdId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/firms/{firmId}/orders/{clOrdId}/clear-stale");
        req.Headers.Authorization = adminAuth;
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.NoContent,
            $"POST /admin/firms/{firmId}/orders/{clOrdId}/clear-stale expected 204 NoContent, got {(int)resp.StatusCode}: {body}");
    }

    private static async Task CancelOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/orders/{clOrdId}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.NoContent,
            $"DELETE /orders/{clOrdId} expected 204 NoContent, got {(int)resp.StatusCode}: {body}");
    }

    private static async Task TryCleanupRecoveredOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue adminAuth,
        AuthenticationHeaderValue auth,
        string firmId,
        ulong clOrdId,
        bool isStale)
    {
        try
        {
            if (isStale)
                await ClearStaleAsync(http, adminAuth, firmId, clOrdId);

            await CancelOrderAsync(http, auth, clOrdId);
        }
        catch
        {
        }
    }

    private static async Task AssertPostRecoveryTradingRoundTripAsync(
        HttpClient http,
        AuthenticationHeaderValue buyAuth,
        AuthenticationHeaderValue sellAuth,
        DockerVenueTransportController docker,
        string symbol,
        decimal price,
        long quantity)
    {
        var submitStartUtc = DateTimeOffset.UtcNow;
        var buyClOrdId = await SubmitOrderAsync(http, buyAuth, symbol, price, side: "Buy", quantity: quantity);
        await WaitForOrderAsync(
            http,
            buyAuth,
            buyClOrdId,
            order => order.Status == "Working" && order.CumulativeQuantity == 0,
            OrderTimeout,
            "post-restart buy order to reach Working before the cross");

        var sellClOrdId = await SubmitOrderAsync(http, sellAuth, symbol, price, side: "Sell", quantity: quantity);
        await WaitForOrderAsync(
            http,
            buyAuth,
            buyClOrdId,
            order => order.Status == "Filled" && order.CumulativeQuantity == quantity,
            TradeTimeout,
            "post-restart buy order to reach Filled");
        await WaitForOrderAsync(
            http,
            sellAuth,
            sellClOrdId,
            order => order.Status == "Filled" && order.CumulativeQuantity == quantity,
            TradeTimeout,
            "post-restart sell order to reach Filled");

        await docker.WaitForMarketDataTradeDrainAsync(submitStartUtc, TradeTimeout);
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
                    LeavesQuantity: order.GetProperty("leavesQuantity").GetInt64(),
                    CumulativeQuantity: order.GetProperty("cumulativeQuantity").GetInt64(),
                    IsStale: order.TryGetProperty("isStale", out var staleProp) && staleProp.GetBoolean(),
                    StaleReason: order.TryGetProperty("staleReason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String
                        ? reasonProp.GetString()
                        : null);
            }
        }

        return null;
    }

    private static async Task<TradeStateSnapshot> GetTradeStateAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol)
    {
        var balanceTask = GetBalanceAsync(http, auth);
        var positionsTask = GetPositionsAsync(http, auth);
        var pnlTask = GetPnlAsync(http, auth);
        await Task.WhenAll(balanceTask, positionsTask, pnlTask);

        var position = positionsTask.Result.FirstOrDefault(p => p.Symbol == symbol)
                       ?? new PositionSnapshot(symbol, 0, 0m);
        var realized = pnlTask.Result.Realized.FirstOrDefault(p => p.Symbol == symbol)?.Value ?? 0m;

        return new TradeStateSnapshot(
            AvailableBalance: balanceTask.Result,
            PositionNetQuantity: position.NetQuantity,
            PositionAverageEntryPrice: position.AverageEntryPrice,
            RealizedPnl: realized,
            TotalRealizedPnl: pnlTask.Result.TotalRealized);
    }

    private static async Task<TradeStateSnapshot> WaitForTradeStateAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        Func<TradeStateSnapshot, bool> predicate,
        string expectation)
    {
        var deadline = DateTimeOffset.UtcNow + OrderTimeout;
        TradeStateSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await GetTradeStateAsync(http, auth, symbol);
            if (last is not null && predicate(last))
                return last;

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {OrderTimeout.TotalSeconds:F0}s waiting for {expectation}. Last observed={Format(last)}.");
        return null!;
    }

    private static Task<TradeStateSnapshot> WaitForTradeStateAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        TradeStateSnapshot expected,
        string symbol,
        string expectation) =>
        WaitForTradeStateAsync(
            http,
            auth,
            symbol,
            state => state == expected,
            $"{expectation}. Expected={Format(expected)}");

    private static async Task<decimal> GetBalanceAsync(HttpClient http, AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/balance");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("available").GetDecimal();
    }

    private static async Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(HttpClient http, AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/positions");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
        if (json is null)
            return Array.Empty<PositionSnapshot>();

        return json.Select(p => new PositionSnapshot(
                Symbol: p.GetProperty("symbol").GetString()!,
                NetQuantity: p.GetProperty("netQuantity").GetInt64(),
                AverageEntryPrice: p.GetProperty("averageEntryPrice").GetDecimal()))
            .ToArray();
    }

    private static async Task<PnlSnapshot> GetPnlAsync(HttpClient http, AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/pnl/today");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var realized = new List<PnlEntrySnapshot>();
        foreach (var entry in json.GetProperty("realized").EnumerateArray())
        {
            realized.Add(new PnlEntrySnapshot(
                Symbol: entry.GetProperty("symbol").GetString()!,
                Value: entry.GetProperty("value").GetDecimal()));
        }

        return new PnlSnapshot(
            realized,
            json.GetProperty("totalRealized").GetDecimal());
    }

    private static async Task<FirmSnapshot> GetFirmSnapshotAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string firmId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/firms");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var firm in json.GetProperty("firms").EnumerateArray())
        {
            if (firm.GetProperty("firmId").GetString() == firmId)
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

        throw new InvalidOperationException($"Firm '{firmId}' not found in /admin/firms response.");
    }

    private static uint GetUInt32Flexible(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetUInt32(),
        JsonValueKind.String => uint.Parse(value.GetString()!),
        _ => throw new InvalidOperationException($"Expected uint-compatible sessionVerId, observed {value.ValueKind}."),
    };

    private static string Format(OrderSnapshot? order) => order is null
        ? "<missing>"
        : $"{{ status={order.Status}, leavesQuantity={order.LeavesQuantity}, cumulativeQuantity={order.CumulativeQuantity}, isStale={order.IsStale}, staleReason={order.StaleReason ?? "null"} }}";

    private static string Format(FirmSnapshot? firm) => firm is null
        ? "<missing>"
        : $"{{ sessionState={firm.SessionState ?? "null"}, sessionVerId={firm.SessionVerId}, reconnecting={firm.Reconnecting} }}";

    private static string Format(TradeStateSnapshot? state) => state is null
        ? "<missing>"
        : $"{{ availableBalance={state.AvailableBalance}, positionNetQuantity={state.PositionNetQuantity}, positionAverageEntryPrice={state.PositionAverageEntryPrice}, realizedPnl={state.RealizedPnl}, totalRealizedPnl={state.TotalRealizedPnl} }}";

    private static decimal CalculateExpectedAverageEntryPrice(
        long baselineQuantity,
        decimal baselineAverageEntryPrice,
        long fillQuantity,
        decimal fillPrice)
    {
        if (baselineQuantity <= 0)
            return fillPrice;

        var totalCost = (baselineQuantity * baselineAverageEntryPrice) + (fillQuantity * fillPrice);
        return decimal.Round(totalCost / (baselineQuantity + fillQuantity), 10, MidpointRounding.AwayFromZero);
    }

    private sealed record OrderSnapshot(
        string Status,
        long LeavesQuantity,
        long CumulativeQuantity,
        bool IsStale,
        string? StaleReason);

    private sealed record FirmSnapshot(
        string? SessionState,
        uint SessionVerId,
        bool Reconnecting);

    private sealed record PositionSnapshot(
        string Symbol,
        long NetQuantity,
        decimal AverageEntryPrice);

    private sealed record PnlEntrySnapshot(
        string Symbol,
        decimal Value);

    private sealed record PnlSnapshot(
        IReadOnlyList<PnlEntrySnapshot> Realized,
        decimal TotalRealized);

    private sealed record TradeStateSnapshot(
        decimal AvailableBalance,
        long PositionNetQuantity,
        decimal PositionAverageEntryPrice,
        decimal RealizedPnl,
        decimal TotalRealizedPnl);
}
