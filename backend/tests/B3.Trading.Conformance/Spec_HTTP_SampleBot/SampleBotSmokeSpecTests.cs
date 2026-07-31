using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_SampleBot;

/// <summary>
/// Spec — sample-bot smoke (#722). Verifies the REST-observable outcome
/// of the one-shot <c>B3.Trading.SampleBot</c> container after it has run
/// against the sample-bot overlay
/// (<c>docker-compose.sample-bot.yml</c> + <c>docker-compose.sample-bot-conformance.yml</c>):
/// at least one terminal order for the dedicated sample-bot end-client,
/// and no Working/PartiallyFilled order left resting once the run is
/// done. This test does NOT itself start the sample-bot container — CI
/// (<c>sample-bot-conformance</c> job in <c>.github/workflows/docker.yml</c>)
/// runs the one-shot bot to completion first, then this spec verifies the
/// resulting state purely over the participant REST API, exactly as an
/// external auditor/operator would.
/// </summary>
/// <remarks>
/// <para>
/// Gated on <see cref="ConformanceFactAttribute.RequiresSampleBotSandbox"/>
/// (operator/CI sets <c>B3T_SAMPLE_BOT_SANDBOX=true</c>) rather than any of
/// the existing sandbox flags: the sample-bot's single working order
/// belongs to a dedicated <c>sample-bot</c> end-client
/// (<c>B3T_SAMPLE_BOT_USER</c>/<c>B3T_SAMPLE_BOT_PASS</c>, seeded by
/// <c>docker-compose.sample-bot.yml</c>'s <c>Trading__Auth__Users__9</c>),
/// never alice/bob — so this spec never shares order/position state with
/// any other conformance scenario.
/// </para>
/// <para>
/// Per the sample-bot's own <c>ComputePassiveLimitPrice</c> (see
/// <c>SampleBotWorkflow</c>), the default Buy is deliberately priced away
/// from the observed reference so it does NOT rely on crossing the
/// market-maker bot's resting quotes. The deterministic journey is
/// submit -&gt; Working (observed by the bot over its own private WS) -&gt;
/// <c>OrderTimeout</c> -&gt; best-effort cancel -&gt; terminal Cancelled. An
/// unexpected Filled is still accepted here (a passive order can always
/// be crossed by fresh third-party flow) — what this spec actually
/// guards is "no order left Working/PartiallyFilled", not a specific
/// terminal status.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
public class SampleBotSmokeSpecTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly IReadOnlySet<string> TerminalStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Filled", "Cancelled", "Canceled" };

    private static readonly IReadOnlySet<string> LiveStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Working", "PartiallyFilled" };

    [ConformanceFact(RequiresSampleBotSandbox = true)]
    public async Task OneShotSampleBot_ProducesTerminalOrder_AndLeavesNoWorkingOrder()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        var (username, password) = ResolveSampleBotCredentials();

        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var auth = await LoginHelper.LoginAsync(http, username, password);

        // The one-shot sample-bot container already ran to completion
        // before this spec executes (CI orders the steps that way).
        // Poll rather than assume instantaneous consistency: the last
        // authoritative execution report can lag the container's own
        // exit by a beat.
        var orders = await WaitForAtLeastOneOrderAsync(http, auth);
        Assert.True(orders.Count > 0,
            "Expected GET /api/orders to report at least one order for the sample-bot end-client " +
            "after the one-shot container ran — did the sample-bot service actually execute " +
            "(docker compose run --rm sample-bot) before this spec?");

        // This verifier is deliberately observational: cancelling a leaked
        // order here would hide the exact cleanup regression the smoke exists
        // to detect. The CI job tears down the isolated stack after the test.
        var finalOrders = await WaitForNoLiveOrdersAsync(http, auth);

        var terminal = finalOrders.Where(o => TerminalStatuses.Contains(o.Status)).ToList();
        Assert.True(terminal.Count > 0,
            $"Expected at least one successful terminal order (Filled/Cancelled) for the sample-bot " +
            $"end-client. Observed statuses: {string.Join(", ", finalOrders.Select(o => o.Status))}");

        var live = finalOrders.Where(o => LiveStatuses.Contains(o.Status)).ToList();
        Assert.True(live.Count == 0,
            $"Expected no Working/PartiallyFilled sample-owned order after the one-shot run. " +
            $"Remaining: {string.Join(", ", live.Select(o => $"{o.ClOrdId}={o.Status}"))}");
    }

    private static (string Username, string Password) ResolveSampleBotCredentials()
    {
        var username = Environment.GetEnvironmentVariable("B3T_SAMPLE_BOT_USER");
        var password = Environment.GetEnvironmentVariable("B3T_SAMPLE_BOT_PASS");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "B3T_SAMPLE_BOT_USER / B3T_SAMPLE_BOT_PASS are required when B3T_SAMPLE_BOT_SANDBOX=true " +
                "(docker-compose.sample-bot-conformance.yml sets both).");
        }

        return (username, password);
    }

    private static async Task<List<OrderSnapshot>> WaitForAtLeastOneOrderAsync(HttpClient http, AuthenticationHeaderValue auth)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        List<OrderSnapshot> last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await ListOrdersAsync(http, auth);
            if (last.Count > 0)
                return last;
            await Task.Delay(PollInterval);
        }

        return last;
    }

    private static async Task<List<OrderSnapshot>> WaitForNoLiveOrdersAsync(HttpClient http, AuthenticationHeaderValue auth)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        List<OrderSnapshot> last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await ListOrdersAsync(http, auth);
            if (last.Count > 0 && last.All(o => !LiveStatuses.Contains(o.Status)))
                return last;
            await Task.Delay(PollInterval);
        }

        return last;
    }

    private static async Task<List<OrderSnapshot>> ListOrdersAsync(HttpClient http, AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var orders = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
        if (orders is null)
            return [];

        var result = new List<OrderSnapshot>();
        foreach (var order in orders)
        {
            result.Add(new OrderSnapshot(
                order.GetProperty("clOrdId").GetString()!,
                order.GetProperty("symbol").GetString() ?? "",
                order.GetProperty("status").GetString()!));
        }

        return result;
    }

    private sealed record OrderSnapshot(string ClOrdId, string Symbol, string Status);
}
