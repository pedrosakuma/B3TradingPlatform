using B3.Trading.EntryPointListener.Tests.Mtls;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

/// <summary>
/// Covers the mTLS / client-certificate validation rules added to
/// <see cref="EntryPointListenerOptionsValidator"/> (RFC user-bot-fixp-mtls-v0 §5.1).
/// </summary>
public sealed class MtlsValidatorTests : IDisposable
{
    private readonly string _dir;
    private readonly EntryPointListenerOptionsValidator _validator = new();

    public MtlsValidatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b3-mtls-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string ValidBundle()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        return CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
    }

    private static EntryPointListenerOptions BaseEnabled() => new()
    {
        Enabled = true,
        Endpoint = "127.0.0.1:5001",
    };

    [Fact]
    public void Default_ModeNone_NoMtlsRules_Succeed()
    {
        var opts = BaseEnabled();
        Assert.True(_validator.Validate(null, opts).Succeeded);
    }

    [Fact]
    public void MtlsEnabled_WithoutTlsRequired_Fails()
    {
        var opts = BaseEnabled();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.Required = false;
        opts.Tls.ClientCa.BundlePath = ValidBundle();

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ClientCertificateMode") && f.Contains("Tls:Required"));
    }

    [Fact]
    public void MtlsEnabled_MissingBundlePath_Fails()
    {
        var opts = BaseEnabled();
        opts.Tls.Required = true;
        opts.Tls.CertPath = WriteServerPfx();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Optional;
        opts.Tls.ClientCa.BundlePath = null;

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ClientCa:BundlePath"));
    }

    [Fact]
    public void MtlsEnabled_UnparseableBundle_Fails()
    {
        var badBundle = Path.Combine(_dir, "bad.pem");
        File.WriteAllText(badBundle, "this is not a certificate");

        var opts = BaseEnabled();
        opts.Tls.Required = true;
        opts.Tls.CertPath = WriteServerPfx();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = badBundle;

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("not a usable PEM CA bundle"));
    }

    [Fact]
    public void MtlsEnabled_DenyListPathMissing_Fails()
    {
        var opts = BaseEnabled();
        opts.Tls.Required = true;
        opts.Tls.CertPath = WriteServerPfx();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = ValidBundle();
        opts.Tls.ClientCa.DenyListPath = Path.Combine(_dir, "missing-deny.txt");

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("DenyListPath"));
    }

    [Fact]
    public void MtlsEnabled_NonPositiveReloadInterval_Fails()
    {
        var opts = BaseEnabled();
        opts.Tls.Required = true;
        opts.Tls.CertPath = WriteServerPfx();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = ValidBundle();
        opts.Tls.ClientCa.ReloadInterval = TimeSpan.Zero;

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ReloadInterval"));
    }

    [Fact]
    public void MtlsEnabled_FullyConfigured_Succeeds()
    {
        var opts = BaseEnabled();
        opts.Tls.Required = true;
        opts.Tls.CertPath = WriteServerPfx();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = ValidBundle();
        opts.Tls.ClientCa.ReloadInterval = TimeSpan.FromMinutes(5);

        Assert.True(_validator.Validate(null, opts).Succeeded);
    }

    private string WriteServerPfx()
    {
        // A .pfx CertPath avoids requiring a separate KeyPath for the
        // server-TLS rule, keeping these tests focused on the mTLS rules.
        using var cert = CertTestUtil.CreateCaCertificate("B3 Server");
        var path = Path.Combine(_dir, "server.pfx");
        File.WriteAllBytes(path, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        return path;
    }
}
