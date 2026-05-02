using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

public class AdminFirmsEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AdminFirmsEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task AdminFirms_RequiresAdminRole()
    {
        using var userClient = await _factory.CreateAuthedClientAsync(); // alice (user role)
        var resp = await userClient.GetAsync("/admin/firms");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task AdminFirms_ReturnsConfiguredShapeInMockMode()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync("/admin/firms");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<FirmsResponse>(JsonOptions);
        Assert.NotNull(body);
        // TestAppFactory configures one firm: { FirmId = "TEST" }; live state
        // fields are null because Mock mode doesn't register FirmGatewayRegistry.
        Assert.Equal("Mock", body!.Mode);
        var firm = Assert.Single(body.Firms);
        Assert.Equal("TEST", firm.FirmId);
        Assert.Null(firm.SessionState);
        Assert.Null(firm.SessionVerId);
        Assert.Null(firm.Reconnecting);
    }

    private sealed record FirmsResponse(string Mode, FirmEntry[] Firms);
    private sealed record FirmEntry(
        string FirmId,
        string Endpoint,
        uint SessionId,
        string? SessionState,
        uint? SessionVerId,
        bool? Reconnecting);
}
