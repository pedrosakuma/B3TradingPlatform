using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using B3.Trading.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests.Lifecycle;

/// <summary>
/// /health surfaces live FIXP session state when an
/// <see cref="IFirmSessionStatusProvider"/> is registered (Real mode wires
/// it via FirmGatewayRegistry). Without a provider (Mock/Stub) the response
/// keeps the legacy shape — no firms[] array, readyForOrders driven by
/// <see cref="ExchangeStatus.ReadyForOrders"/> alone — so existing smoke
/// tests / dashboards keep working.
/// </summary>
public class HealthSessionStatusTests
{
    private sealed class FakeProvider : IFirmSessionStatusProvider
    {
        private readonly IReadOnlyList<FirmSessionStatus> _snapshot;
        public FakeProvider(params FirmSessionStatus[] snapshot) => _snapshot = snapshot;
        public IReadOnlyList<FirmSessionStatus> Snapshot() => _snapshot;
    }

    private static WebApplicationFactory<Program> WithProvider(IFirmSessionStatusProvider? provider) =>
        new TestAppFactory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            // Mock-mode default doesn't register the provider; tests that
            // need one inject a fake here so we don't have to spin up a
            // real EntryPointClient just to assert /health JSON shape.
            if (provider is not null)
                s.AddSingleton(provider);
        }));

    [Fact]
    public async Task NoProvider_LegacyShape_NoFirmsArray()
    {
        using var factory = WithProvider(provider: null);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var exchange = doc.RootElement.GetProperty("exchange");
        Assert.True(exchange.GetProperty("readyForOrders").GetBoolean(),
            "Mock-mode test host should remain ready when no live session info is available.");
        Assert.False(exchange.TryGetProperty("firms", out _),
            "Legacy /health consumers must not see a firms[] field when no provider is wired.");
    }

    [Fact]
    public async Task AllFirmsEstablished_ReadyTrue_FirmsArrayPresent()
    {
        var fake = new FakeProvider(
            new FirmSessionStatus("FIRM01", "established", IsReconnecting: false, SessionVerId: 7),
            new FirmSessionStatus("FIRM02", "established", IsReconnecting: false, SessionVerId: 3));
        using var factory = WithProvider(fake);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var exchange = doc.RootElement.GetProperty("exchange");
        Assert.True(exchange.GetProperty("readyForOrders").GetBoolean());

        var firms = exchange.GetProperty("firms");
        Assert.Equal(2, firms.GetArrayLength());
        Assert.Equal("FIRM01", firms[0].GetProperty("firmId").GetString());
        Assert.Equal("established", firms[0].GetProperty("state").GetString());
        Assert.False(firms[0].GetProperty("reconnecting").GetBoolean());
        Assert.Equal(7u, firms[0].GetProperty("sessionVerId").GetUInt32());

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task AnyFirmNotEstablished_ReadyFalse()
    {
        // Reproduces the dogfood bug from issue #137: a Real-mode firm with
        // a suspended session was still surfacing readyForOrders=true,
        // leaving the UI badge green while submits were rejected by the
        // SDK guard ("Client is not in Established state").
        var fake = new FakeProvider(
            new FirmSessionStatus("FIRM01", "established", IsReconnecting: false, SessionVerId: 1),
            new FirmSessionStatus("FIRM02", "suspended", IsReconnecting: true, SessionVerId: 4));
        using var factory = WithProvider(fake);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var exchange = doc.RootElement.GetProperty("exchange");

        Assert.False(exchange.GetProperty("readyForOrders").GetBoolean(),
            "readyForOrders must be false while any configured firm is not established.");
        var firms = exchange.GetProperty("firms");
        Assert.Equal("suspended", firms[1].GetProperty("state").GetString());
        Assert.True(firms[1].GetProperty("reconnecting").GetBoolean());

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        var live = await client.GetAsync("/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task EmptyFirmList_IsNotReady_WhenConfiguredFirmSessionIsMissing()
    {
        using var factory = WithProvider(new FakeProvider());
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var exchange = doc.RootElement.GetProperty("exchange");

        Assert.False(exchange.GetProperty("readyForOrders").GetBoolean());
        Assert.Equal(0, exchange.GetProperty("firms").GetArrayLength());

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }
}
