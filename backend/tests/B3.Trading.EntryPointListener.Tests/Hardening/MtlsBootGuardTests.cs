using B3.Trading.EntryPointListener;
using B3.Trading.EntryPointListener.Tests.Mtls;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

/// <summary>
/// Production boot-guard rules for mTLS (RFC user-bot-fixp-mtls-v0 §7).
/// <see cref="ClientCertificateMode.None"/>/<see cref="ClientCertificateMode.Optional"/>
/// warn but boot; <see cref="ClientCertificateMode.Required"/> fails closed
/// unless its CA bundle (and a deny-list, absent the explicit opt-in) is
/// fully configured.
/// </summary>
public sealed class MtlsBootGuardTests : IDisposable
{
    private readonly string _dir;

    public MtlsBootGuardTests()
    {
        _dir = Path.Combine(AppContext.BaseDirectory, "MtlsBootGuard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteCaBundle(string fileName = "ca.pem")
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        return CertTestUtil.WritePemBundle(_dir, fileName, ca);
    }

    private static EntryPointListenerOptions BaseProdOptions() => new()
    {
        Enabled = true,
        Endpoint = "0.0.0.0:5001",
        AllowInProduction = true,
        Tls = new EntryPointListenerOptions.TlsOptions
        {
            Required = true,
            CertPath = "/etc/ssl/server.crt",
            KeyPath = "/etc/ssl/server.key",
        },
    };

    // ─── Validate: Required mode ──────────────────────────────────────────────

    [Fact]
    public void Validate_Production_Required_NoBundle_Throws()
    {
        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("BundlePath", ex.Message);
    }

    [Fact]
    public void Validate_Production_Required_BundleParseFails_Throws()
    {
        var bogus = Path.Combine(_dir, "bogus.pem");
        File.WriteAllText(bogus, "not a certificate");

        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = bogus;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("could not be parsed", ex.Message);
    }

    [Fact]
    public void Validate_Production_Required_BundleButNoDenyList_Throws()
    {
        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = WriteCaBundle();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntryPointListenerBootGuard.Validate(Environments.Production, opts));
        Assert.Contains("DenyListPath", ex.Message);
    }

    [Fact]
    public void Validate_Production_Required_NoDenyList_AllowInsecure_DoesNotThrow()
    {
        var opts = BaseProdOptions();
        opts.AllowInsecureMtlsInProduction = true;
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = WriteCaBundle();

        EntryPointListenerBootGuard.Validate(Environments.Production, opts);
    }

    [Fact]
    public void Validate_Production_Required_BundleAndDenyList_DoesNotThrow()
    {
        var denyPath = Path.Combine(_dir, "deny.txt");
        File.WriteAllText(denyPath, string.Empty);

        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = WriteCaBundle();
        opts.Tls.ClientCa.DenyListPath = denyPath;

        EntryPointListenerBootGuard.Validate(Environments.Production, opts);
    }

    // ─── Validate: None / Optional always boot ────────────────────────────────

    [Theory]
    [InlineData(ClientCertificateMode.None)]
    [InlineData(ClientCertificateMode.Optional)]
    public void Validate_Production_NoneOrOptional_DoesNotThrow(ClientCertificateMode mode)
    {
        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = mode;

        EntryPointListenerBootGuard.Validate(Environments.Production, opts);
    }

    [Fact]
    public void Validate_NonProduction_RequiredWithoutBundle_DoesNotThrow()
    {
        var opts = new EntryPointListenerOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:5001",
            Tls = new EntryPointListenerOptions.TlsOptions { Required = true },
        };
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;

        EntryPointListenerBootGuard.Validate("Development", opts);
    }

    // ─── BuildWarning: mTLS line per mode ─────────────────────────────────────

    [Fact]
    public void BuildWarning_Production_None_LoudWhenNotOptedIn()
    {
        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.None;

        var msg = EntryPointListenerBootGuard.BuildWarning(Environments.Production, opts);
        Assert.NotNull(msg);
        Assert.Contains("mTLS: None", msg!);
        Assert.Contains("PAT alone", msg);
    }

    [Fact]
    public void BuildWarning_Production_None_QuietWhenOptedIn()
    {
        var opts = BaseProdOptions();
        opts.AllowInsecureMtlsInProduction = true;
        opts.Tls.ClientCertificateMode = ClientCertificateMode.None;

        var msg = EntryPointListenerBootGuard.BuildWarning(Environments.Production, opts);
        Assert.NotNull(msg);
        Assert.Contains("mTLS: None.", msg!);
        Assert.DoesNotContain("PAT alone", msg);
    }

    [Fact]
    public void BuildWarning_Production_Optional_LoudWhenNotOptedIn()
    {
        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Optional;
        opts.Tls.ClientCa.BundlePath = WriteCaBundle();

        var msg = EntryPointListenerBootGuard.BuildWarning(Environments.Production, opts);
        Assert.NotNull(msg);
        Assert.Contains("mTLS: Optional", msg!);
        Assert.Contains("WITHOUT a certificate", msg);
    }

    [Fact]
    public void BuildWarning_Required_ReportsBundleAndDenyListCount()
    {
        var denyPath = Path.Combine(_dir, "deny.txt");
        using var leaf = CertTestUtil.CreateCaCertificate("revoked");
        File.WriteAllText(denyPath, CertTestUtil.Sha256Thumbprint(leaf) + Environment.NewLine);

        var bundle = WriteCaBundle();
        var opts = BaseProdOptions();
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = bundle;
        opts.Tls.ClientCa.DenyListPath = denyPath;

        var msg = EntryPointListenerBootGuard.BuildWarning(Environments.Production, opts);
        Assert.NotNull(msg);
        Assert.Contains("mTLS: Required", msg!);
        Assert.Contains(bundle, msg);
        Assert.Contains("1 entries", msg);
    }

    [Fact]
    public void BuildWarning_Required_NoDenyList_ReportsNone()
    {
        var opts = BaseProdOptions();
        opts.AllowInsecureMtlsInProduction = true;
        opts.Tls.ClientCertificateMode = ClientCertificateMode.Required;
        opts.Tls.ClientCa.BundlePath = WriteCaBundle();

        var msg = EntryPointListenerBootGuard.BuildWarning(Environments.Production, opts);
        Assert.NotNull(msg);
        Assert.Contains("deny-list: none", msg!);
    }
}
