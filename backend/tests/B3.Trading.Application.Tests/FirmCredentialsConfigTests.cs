using System.Runtime.InteropServices;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #126. Coverage for <see cref="FirmCredentialResolver"/> + the new
/// <see cref="FirmCredentialsConfig"/> shape: legacy compat, file-mounted
/// indirection, Linux permission enforcement, and the validator's
/// per-mode shape checks.
/// </summary>
public class FirmCredentialsConfigTests : IDisposable
{
    private readonly string _tmpRoot = Path.Combine(
        Path.GetTempPath(), "b3-firmcreds-" + Guid.NewGuid().ToString("N"));

    public FirmCredentialsConfigTests() => Directory.CreateDirectory(_tmpRoot);

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpRoot)) Directory.Delete(_tmpRoot, recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static FirmConfig BaseFirm(string firmId = "FIRM_T") => new()
    {
        FirmId = firmId,
        Endpoint = "broker.example.com:9000",
        SessionId = 100,
        SessionVerId = 1,
        EnteringFirm = 200,
        SenderLocation = "BR-SP",
        EnteringTrader = "TR1",
        KeepAliveIntervalMs = 1000,
    };

    [Fact]
    public void Resolve_LegacyAccessKey_Returns_Literal()
    {
        var firm = BaseFirm();
        firm.AccessKey = "legacy-secret";
        var key = FirmCredentialResolver.ResolveAccessKey(firm, NullLogger.Instance);
        Assert.Equal("legacy-secret", key);
    }

    [Fact]
    public void Resolve_StructuredInlineAccessKey_Returns_Literal()
    {
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKey = "structured-secret",
        };
        Assert.Equal("structured-secret", FirmCredentialResolver.ResolveAccessKey(firm));
    }

    [Fact]
    public void Resolve_StructuredWinsOverLegacy()
    {
        var firm = BaseFirm();
        firm.AccessKey = "legacy";
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKey = "wins",
        };
        Assert.Equal("wins", FirmCredentialResolver.ResolveAccessKey(firm));
    }

    [Fact]
    public void Resolve_NoCredentials_Throws()
    {
        var firm = BaseFirm();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirmCredentialResolver.ResolveAccessKey(firm));
        Assert.Contains("no credentials configured", ex.Message);
    }

    [Fact]
    public void Resolve_StructuredEmpty_Throws()
    {
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig { Mode = FirmCredentialsMode.AccessKey };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirmCredentialResolver.ResolveAccessKey(firm));
        Assert.Contains("requires either AccessKey or AccessKeyFile", ex.Message);
    }

    [Fact]
    public void Resolve_StructuredBothInlineAndFile_Throws()
    {
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKey = "x",
            AccessKeyFile = "/tmp/x",
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirmCredentialResolver.ResolveAccessKey(firm));
        Assert.Contains("exactly one is required", ex.Message);
    }

    [Fact]
    public void Resolve_AccessKeyFile_ReadsAndTrims()
    {
        var path = WriteSecretFile("file-secret\n", mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKeyFile = path,
        };
        Assert.Equal("file-secret", FirmCredentialResolver.ResolveAccessKey(firm));
    }

    [Fact]
    public void Resolve_AccessKeyFile_MissingPath_Throws()
    {
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKeyFile = Path.Combine(_tmpRoot, "does-not-exist"),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirmCredentialResolver.ResolveAccessKey(firm));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void Resolve_AccessKeyFile_EmptyFile_Throws()
    {
        var path = WriteSecretFile("   \n\t", mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKeyFile = path,
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirmCredentialResolver.ResolveAccessKey(firm));
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void Resolve_AccessKeyFile_AllowsMode400()
    {
        var path = WriteSecretFile("read-only-secret", mode: UnixFileMode.UserRead);
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKeyFile = path,
        };
        Assert.Equal("read-only-secret", FirmCredentialResolver.ResolveAccessKey(firm));
    }

    [Fact]
    public void Resolve_AccessKeyFile_GroupReadable_ThrowsOnLinux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return; // permission check is Linux-only

        var path = WriteSecretFile("leaky", mode:
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead);
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKeyFile = path,
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirmCredentialResolver.ResolveAccessKey(firm));
        Assert.Contains("insecure permissions", ex.Message);
        Assert.Contains("must be 600 or 400", ex.Message);
    }

    [Fact]
    public void Resolve_AccessKeyFile_WorldReadable_ThrowsOnLinux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var path = WriteSecretFile("worldleak", mode:
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.OtherRead);
        var firm = BaseFirm();
        firm.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKeyFile = path,
        };
        Assert.Throws<InvalidOperationException>(() =>
            FirmCredentialResolver.ResolveAccessKey(firm));
    }

    [Fact]
    public void ToString_RedactsSecretMaterial()
    {
        var creds = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKey = "super-secret-key",
            AccessKeyFile = "/run/secrets/x",
        };
        var s = creds.ToString();
        Assert.DoesNotContain("super-secret-key", s);
        Assert.Contains("redacted:", s);
        Assert.Contains("/run/secrets/x", s); // path is non-sensitive
    }

    // --------------------------------------------------------------
    // Validator coverage — per-mode shape checks
    // --------------------------------------------------------------

    private static FirmConfig ValidRealFirm(string firmId = "FIRM_A")
    {
        var f = BaseFirm(firmId);
        f.AccessKey = "secret"; // legacy shape, valid
        return f;
    }

    [Fact]
    public void Validator_LegacyAccessKey_Accepts()
    {
        var opts = new ExchangeOptions { Mode = ExchangeMode.Real, Firms = { ValidRealFirm() } };
        Assert.True(new ExchangeOptionsValidator().Validate(null, opts).Succeeded);
    }

    [Fact]
    public void Validator_StructuredCredentialsOnly_Accepts()
    {
        var f = BaseFirm();
        f.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKey = "x",
        };
        var opts = new ExchangeOptions { Mode = ExchangeMode.Real, Firms = { f } };
        Assert.True(new ExchangeOptionsValidator().Validate(null, opts).Succeeded);
    }

    [Fact]
    public void Validator_NeitherLegacyNorStructured_Fails()
    {
        var f = BaseFirm(); // no AccessKey, no Credentials
        var opts = new ExchangeOptions { Mode = ExchangeMode.Real, Firms = { f } };
        var result = new ExchangeOptionsValidator().Validate(null, opts);
        Assert.False(result.Succeeded);
        Assert.Contains("AccessKey or", string.Join(";", result.Failures!));
    }

    [Fact]
    public void Validator_StructuredCredentialsEmpty_Fails()
    {
        var f = BaseFirm();
        f.Credentials = new FirmCredentialsConfig { Mode = FirmCredentialsMode.AccessKey };
        var opts = new ExchangeOptions { Mode = ExchangeMode.Real, Firms = { f } };
        var result = new ExchangeOptionsValidator().Validate(null, opts);
        Assert.False(result.Succeeded);
        Assert.Contains("requires either AccessKey or AccessKeyFile", string.Join(";", result.Failures!));
    }

    [Fact]
    public void Validator_StructuredCredentialsBothFields_Fails()
    {
        var f = BaseFirm();
        f.Credentials = new FirmCredentialsConfig
        {
            Mode = FirmCredentialsMode.AccessKey,
            AccessKey = "x",
            AccessKeyFile = "/tmp/x",
        };
        var opts = new ExchangeOptions { Mode = ExchangeMode.Real, Firms = { f } };
        var result = new ExchangeOptionsValidator().Validate(null, opts);
        Assert.False(result.Succeeded);
        Assert.Contains("exactly one is required", string.Join(";", result.Failures!));
    }

    // --------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------

    private string WriteSecretFile(string content, UnixFileMode mode)
    {
        var path = Path.Combine(_tmpRoot, "secret-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            File.SetUnixFileMode(path, mode);
        return path;
    }
}
