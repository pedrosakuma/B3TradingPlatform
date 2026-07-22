using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using Microsoft.Extensions.DependencyInjection;
using xRetry;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.2 (#302). Every order-flow counter (submit / reject / gateway
/// failure / cancel / modify / realised P&amp;L) must carry a
/// <c>firmId</c> tag so Prometheus dashboards can slice volumes and
/// rejections per firm. This test posts an order through the full
/// pipeline and asserts the <c>firmId</c> tag is present on the
/// emitted <c>trading.orders.submitted</c> measurement.
/// </summary>
public class MetricsFirmTagTests
{
    // MeterListener race with parallel tests; retry 3x.
    [RetryFact(maxRetries: 3, delayBetweenRetriesMs: 250)]
    public async Task OrdersSubmittedCounter_CarriesFirmIdTag()
    {
        // Start the MeterListener BEFORE building the TestAppFactory so the
        // InstrumentPublished callback observes "trading.orders.submitted"
        // at registration time. Doing it after the factory races: parallel
        // tests may have already published the instrument on the shared
        // "B3.Trading" Meter on a different thread.
        //
        // The Meter is process-global, so this listener also sees increments
        // emitted by every other TestAppFactory running concurrently in the
        // same xunit collection. We therefore collect every observed firmId
        // into a bag and assert FIRM01 appears at least once, instead of
        // tracking a single "last seen" value that would be raced by sibling
        // factories' "default" firm increments (#332).
        var observedFirms = new System.Collections.Concurrent.ConcurrentBag<string>();
        var firm01Count = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "B3.Trading" && instr.Name == "trading.orders.submitted")
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? firm = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "firmId" && tag.Value is string s)
                {
                    firm = s;
                    observedFirms.Add(s);
                }
            }
            if (firm == "FIRM01")
                Interlocked.Add(ref firm01Count, value);
        });
        listener.Start();

        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            // Re-use the configured FIRM01 user so the order routes
            // through the firm-aware submission path.
            ["Trading:Auth:Users:0:Firm"] = "FIRM01",
        });

        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (token, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM01");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 10,
            Price = 30.0m,
        });
        resp.EnsureSuccessStatusCode();

        // counter.Add runs on a pipeline thread; the MeasurementEventCallback
        // fires asynchronously, so poll instead of asserting synchronously
        // (per CodebaseFact: assert OTel counter increments via bounded poll
        // on Interlocked.Read).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Interlocked.Read(ref firm01Count) < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var seen = Interlocked.Read(ref firm01Count);
        Assert.True(
            seen >= 1,
            $"expected at least one FIRM01 increment, got {seen}; observed firms: [{string.Join(",", observedFirms)}]");
    }
}
