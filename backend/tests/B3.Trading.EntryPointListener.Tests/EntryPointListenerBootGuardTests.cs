using B3.Trading.EntryPointListener;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.EntryPointListener.Tests;

public class EntryPointListenerBootGuardTests
{
    // ─── Validate: production + enabled ──────────────────────────────────────

    [Fact]
    public void Validate_Production_Enabled_NoOptIn_Throws()
    {
        var opts = new EntryPointListenerOptions { Enabled = true, Endpoint = "127.0.0.1:5001" };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("AllowInProduction", ex.Message);
    }

    [Fact]
    public void Validate_Production_Enabled_OptIn_NoTls_Throws()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            AllowInProduction = true,
            // Tls.Required = false (default)
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("Tls:Required=true", ex.Message);
    }

    [Fact]
    public void Validate_Production_Enabled_OptIn_TlsRequired_MissingPaths_Throws()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            AllowInProduction = true,
            Tls = new EntryPointListenerOptions.TlsOptions { Required = true },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("CertPath", ex.Message);
    }

    [Fact]
    public void Validate_Production_Enabled_FullTls_DoesNotThrow()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            AllowInProduction = true,
            Tls = new EntryPointListenerOptions.TlsOptions
            {
                Required = true,
                CertPath = "/etc/ssl/server.crt",
                KeyPath = "/etc/ssl/server.key",
            },
        };
        // Should not throw — path existence is checked by IValidateOptions, not the boot guard.
        EntryPointListenerBootGuard.Validate(Environments.Production, opts);
    }

    // ─── Validate: non-production ─────────────────────────────────────────────

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Validate_NonProduction_Enabled_NeverThrows(string env)
    {
        var opts = new EntryPointListenerOptions { Enabled = true, Endpoint = "127.0.0.1:5001" };
        EntryPointListenerBootGuard.Validate(env, opts);
    }

    // ─── Validate: disabled ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Validate_Disabled_NeverThrows(string env)
    {
        var opts = new EntryPointListenerOptions { Enabled = false };
        EntryPointListenerBootGuard.Validate(env, opts);
    }

    // ─── BuildWarning ────────────────────────────────────────────────────────

    [Fact]
    public void BuildWarning_Disabled_ReturnsNull()
    {
        var opts = new EntryPointListenerOptions { Enabled = false };
        Assert.Null(EntryPointListenerBootGuard.BuildWarning("Development", opts));
        Assert.Null(EntryPointListenerBootGuard.BuildWarning(Environments.Production, opts));
    }

    [Fact]
    public void BuildWarning_Enabled_Development_ReturnsNonNull_WithEndpoint()
    {
        var opts = new EntryPointListenerOptions { Enabled = true, Endpoint = "127.0.0.1:5001" };
        var msg = EntryPointListenerBootGuard.BuildWarning("Development", opts);
        Assert.NotNull(msg);
        Assert.Contains("FIXP LISTENER ENABLED", msg);
        Assert.Contains("127.0.0.1:5001", msg!);
    }

    [Fact]
    public void BuildWarning_Enabled_Production_ContainsProductionNote()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "0.0.0.0:5001",
            AllowInProduction = true,
            Tls = new EntryPointListenerOptions.TlsOptions { Required = true },
        };
        var msg = EntryPointListenerBootGuard.BuildWarning(Environments.Production, opts);
        Assert.NotNull(msg);
        Assert.Contains("PRODUCTION ENVIRONMENT", msg!);
    }
}
