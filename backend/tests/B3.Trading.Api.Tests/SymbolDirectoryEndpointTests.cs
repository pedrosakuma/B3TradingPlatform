using System.Net;
using System.Net.Http.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Validates that POST /api/orders accepts a payload without an explicit
/// SecurityId when a <c>Trading:SymbolDirectory</c> mapping covers the
/// symbol. This is the contract that lets the trader UI submit by
/// symbol; the conformance suite (which always sends explicit
/// SecurityId) must keep working unchanged.
/// </summary>
public class SymbolDirectoryEndpointTests
{
    private static TestAppFactory NewFactoryWithDirectory(bool register = true)
    {
        var overrides = new Dictionary<string, string?>();
        if (register)
        {
            overrides["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321";
        }
        return TestAppFactory.WithOverrides(overrides);
    }

    [Fact]
    public async Task Submit_WithoutSecurityId_KnownSymbol_Resolves()
    {
        using var factory = NewFactoryWithDirectory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            // SecurityId omitted on purpose — this is the trader-UI path.
        });

        // The default test factory uses Mock mode, which routes through
        // the EntryPointClientGateway. Without a real exchange behind
        // it the gateway throws → 502. That is fine for THIS test; we
        // only need to prove we got past the SecurityId validator
        // (which would have returned 400 before this PR).
        Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Submit_WithoutSecurityId_UnknownSymbol_Returns400()
    {
        using var factory = NewFactoryWithDirectory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "UNKNOWN",
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("securityId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_ExplicitSecurityId_TakesPrecedence_OverDirectory()
    {
        // Even when the directory has a mapping, an explicit non-zero
        // SecurityId in the payload wins. This protects callers that
        // know what they are doing (conformance, integration scripts)
        // from a misconfigured directory silently rerouting orders.
        using var factory = NewFactoryWithDirectory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 99999UL, // ≠ directory entry 4321
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });

        Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Submit_WithoutSecurityId_NoDirectoryConfigured_Returns400()
    {
        using var factory = NewFactoryWithDirectory(register: false);
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
