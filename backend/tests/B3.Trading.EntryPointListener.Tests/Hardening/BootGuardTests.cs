using B3.Trading.EntryPointListener;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

public class BootGuardHardeningTests
{
    [Fact]
    public void Validate_Production_MissingTlsCert_Throws()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            AllowInProduction = true,
            Tls = new EntryPointListenerOptions.TlsOptions
            {
                Required = true,
                CertPath = null,
                KeyPath = null,
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("CertPath", ex.Message);
    }

    [Fact]
    public void Validate_Production_MissingTlsRequired_Throws()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            AllowInProduction = true,
            Tls = new EntryPointListenerOptions.TlsOptions
            {
                Required = false,
                CertPath = "/etc/ssl/server.crt",
                KeyPath = "/etc/ssl/server.key",
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("Tls:Required=true", ex.Message);
    }

    [Fact]
    public void Validate_Production_MissingAllowInProduction_Throws()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            AllowInProduction = false,
            Tls = new EntryPointListenerOptions.TlsOptions
            {
                Required = true,
                CertPath = "/etc/ssl/server.crt",
                KeyPath = "/etc/ssl/server.key",
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("AllowInProduction", ex.Message);
    }

    [Fact]
    public void Validate_Production_PfxWithoutKeyPath_Passes()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            AllowInProduction = true,
            Tls = new EntryPointListenerOptions.TlsOptions
            {
                Required = true,
                CertPath = "/etc/ssl/server.pfx",
                KeyPath = null,
            },
        };
        // Should NOT throw — PFX contains the key inside the cert file
        EntryPointListenerBootGuard.Validate(Environments.Production, opts);
    }

    [Fact]
    public void Validate_Production_PemWithoutKeyPath_Throws()
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
                KeyPath = null,
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("KeyPath", ex.Message);
    }
}
