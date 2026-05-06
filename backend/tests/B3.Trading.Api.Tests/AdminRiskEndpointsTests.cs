using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

public class AdminRiskEndpointsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AdminRiskEndpointsTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task GetLimits_RequiresAdminRole()
    {
        using var user = await _factory.CreateAuthedClientAsync(); // role=user
        var resp = await user.GetAsync("/admin/risk/limits?symbol=PETR4");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GetLimits_ReturnsResolvedDefaults_WhenOnlyDefaultIsConfigured()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync("/admin/risk/limits?symbol=PETR4");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var limits = body.GetProperty("limits");
        Assert.Equal(1000, limits.GetProperty("maxQuantity").GetInt64());
        Assert.Equal(1000000m, limits.GetProperty("maxNotional").GetDecimal());
        // MinNotional defaults to null (permissive) — surfaced as JSON null.
        Assert.Equal(JsonValueKind.Null, limits.GetProperty("minNotional").ValueKind);
        Assert.Equal(10m, limits.GetProperty("priceCollarPercent").GetDecimal());
        Assert.Equal(5000, limits.GetProperty("positionLimit").GetInt64());
    }

    [Fact]
    public async Task GetLimits_PerEndClientWinsOverDefault()
    {
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:PerEndClient:alice:MaxQuantity"] = "7",
        });
        using var admin = await f.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync("/admin/risk/limits?endClient=alice&symbol=PETR4");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(7, body.GetProperty("limits").GetProperty("maxQuantity").GetInt64());
    }

    [Fact]
    public async Task GetLimits_PerFirmAppliesWhenEndClientHasNoEntry()
    {
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:PerFirm:broker-a:MaxQuantity"] = "42",
        });
        using var admin = await f.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync("/admin/risk/limits?endClient=bob&firmId=broker-a&symbol=PETR4");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(42, body.GetProperty("limits").GetProperty("maxQuantity").GetInt64());
    }

    [Fact]
    public async Task PostReload_RequiresAdminRole()
    {
        using var user = await _factory.CreateAuthedClientAsync();
        var resp = await user.PostAsync("/admin/risk/reload", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task PostReload_NoOpForAppsettingsProvider_Returns204()
    {
        // No IRiskOptionsReloader is registered in the default test
        // host (we rely on the appsettings file watcher), so the
        // endpoint short-circuits to 204 without doing anything.
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsync("/admin/risk/reload", content: null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }
}
