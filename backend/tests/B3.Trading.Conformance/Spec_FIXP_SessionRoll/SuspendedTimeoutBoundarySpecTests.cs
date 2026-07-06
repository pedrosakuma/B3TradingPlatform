using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

[Trait("Category", "Conformance")]
public class SuspendedTimeoutBoundarySpecTests
{
    private const string FirmId = "FIRM01";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WithinWindowDisconnect = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan PastWindowDisconnect = TimeSpan.FromMilliseconds(5000);

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task WithinSuspendedTimeout_Reattaches_OrderSurvivesNoStaleFlag()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var docker = new DockerVenueTransportController();

        var before = await WaitForFirmEstablishedAsync(http, adminAuth);
        var clOrdId = await SubmitOrderAsync(http, userAuth, "PETR4", 30.00m);
        await WaitForOrderAsync(http, userAuth, clOrdId, order =>
            order.Status == "Working" && !order.IsStale,
            OrderTimeout,
            "order to reach Working before transport interruption");

        var disconnectStartedUtc = DateTimeOffset.UtcNow;
        await using (var detached = await docker.DisconnectMatchingAsync())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await StimulateGatewayWriteAsync(http, userAuth, "ITUB4", 30.50m);
            await DelayUntilAsync(disconnectStartedUtc, WithinWindowDisconnect);
            await detached.ReconnectAsync();
        }
        var after = await WaitForFirmEstablishedAsync(http, adminAuth, priorVerId: before.SessionVerId, expectAdvance: false);
        var orderAfter = await WaitForOrderAsync(http, userAuth, clOrdId, order =>
            order.Status == "Working" && !order.IsStale,
            ReconnectTimeout,
            "order to remain Working and non-stale after reattach");

        Assert.Equal(before.SessionVerId, after.SessionVerId);
        Assert.False(orderAfter.IsStale);
        Assert.Null(orderAfter.StaleReason);
    }

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task PastSuspendedTimeout_Renegotiates_SurvivingOrderFlaggedStale()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var docker = new DockerVenueTransportController();

        var before = await WaitForFirmEstablishedAsync(http, adminAuth);
        var clOrdId = await SubmitOrderAsync(http, userAuth, "VALE3", 60.00m);
        await WaitForOrderAsync(http, userAuth, clOrdId, order =>
            order.Status == "Working" && !order.IsStale,
            OrderTimeout,
            "order to reach Working before transport interruption");

        var disconnectStartedUtc = DateTimeOffset.UtcNow;
        await using (var detached = await docker.DisconnectMatchingAsync())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await StimulateGatewayWriteAsync(http, userAuth, "PETR4", 30.00m);
            await DelayUntilAsync(disconnectStartedUtc, PastWindowDisconnect);
            await detached.ReconnectAsync();
        }
        var after = await WaitForFirmEstablishedAsync(http, adminAuth, priorVerId: before.SessionVerId, expectAdvance: true);
        var orderAfter = await WaitForOrderAsync(http, userAuth, clOrdId, order =>
            order.Status == "Working" && order.IsStale && order.StaleReason?.StartsWith("session_rolled:", StringComparison.Ordinal) == true,
            ReconnectTimeout,
            "order to be marked stale after renegotiated reconnect");

        Assert.True(after.SessionVerId > before.SessionVerId,
            $"Expected sessionVerId to advance past {before.SessionVerId}, observed {after.SessionVerId}.");
        Assert.True(orderAfter.IsStale);
        Assert.StartsWith("session_rolled:", orderAfter.StaleReason);
    }

    private static async Task<ulong> SubmitOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new
            {
                symbol,
                side = "Buy",
                type = "Limit",
                quantity = 100,
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

    private static async Task StimulateGatewayWriteAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new
            {
                symbol,
                side = "Buy",
                type = "Limit",
                quantity = 100,
                price,
            }),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using var _ = await http.SendAsync(req, cts.Token);
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
                    IsStale: order.TryGetProperty("isStale", out var staleProp) && staleProp.GetBoolean(),
                    StaleReason: order.TryGetProperty("staleReason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String
                        ? reasonProp.GetString()
                        : null);
            }
        }

        return null;
    }

    private static async Task<FirmSnapshot> WaitForFirmEstablishedAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        uint? priorVerId = null,
        bool? expectAdvance = null)
    {
        var deadline = DateTimeOffset.UtcNow + ReconnectTimeout;
        FirmSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await GetFirmSnapshotAsync(http, auth);
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

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {ReconnectTimeout.TotalSeconds:F0}s waiting for {FirmId} sessionState=established and sessionVerId expectation {DescribeVerIdExpectation(priorVerId, expectAdvance)}. Last observed={Format(last)}");
        return null!;
    }

    private static async Task<FirmSnapshot> GetFirmSnapshotAsync(
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

    private static async Task DelayUntilAsync(DateTimeOffset startedUtc, TimeSpan targetDuration)
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
        : $"{{ status={order.Status}, isStale={order.IsStale}, staleReason={order.StaleReason ?? "null"} }}";

    private static string Format(FirmSnapshot? firm) => firm is null
        ? "<missing>"
        : $"{{ sessionState={firm.SessionState ?? "null"}, sessionVerId={firm.SessionVerId}, reconnecting={firm.Reconnecting} }}";

    private sealed record OrderSnapshot(
        string Status,
        bool IsStale,
        string? StaleReason);

    private sealed record FirmSnapshot(
        string? SessionState,
        uint SessionVerId,
        bool Reconnecting);
}
