using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using Microsoft.Extensions.DependencyInjection;

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
    [Fact]
    public async Task OrdersSubmittedCounter_CarriesFirmIdTag()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            // Re-use the configured FIRM01 user so the order routes
            // through the firm-aware submission path.
            ["Trading:Auth:Users:0:Firm"] = "FIRM01",
        });

        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (token, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM01");

        string? observedFirmTag = null;
        var observed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "B3.Trading" && instr.Name == "trading.orders.submitted")
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "firmId" && tag.Value is string s)
                    observedFirmTag = s;
            }
            Interlocked.Add(ref observed, value);
        });
        listener.Start();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync("/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 10,
            Price = 30.0m,
        });
        resp.EnsureSuccessStatusCode();

        Assert.True(observed >= 1, $"expected at least one increment, observed {observed}");
        Assert.Equal("FIRM01", observedFirmTag);
    }
}
