using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

public class PolicyEndpointsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PolicyEndpointsTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task GetRiskPolicy_RequiresAuth()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/policy/risk");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GetRiskPolicy_ReturnsConfiguredHorizonDays()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:MaxGtdHorizon"] = "7.00:00:00",
        });
        using var client = await factory.CreateAuthedClientAsync();
        var resp = await client.GetAsync("/policy/risk");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(7, body.GetProperty("maxGtdHorizonDays").GetInt32());
    }

    [Fact]
    public async Task GetRiskPolicy_DefaultsTo30Days()
    {
        using var client = await _factory.CreateAuthedClientAsync();
        var resp = await client.GetAsync("/policy/risk");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(30, body.GetProperty("maxGtdHorizonDays").GetInt32());
    }
}
