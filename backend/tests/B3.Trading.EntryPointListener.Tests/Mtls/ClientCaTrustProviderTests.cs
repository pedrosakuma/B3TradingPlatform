using B3.Trading.EntryPointListener.Mtls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Tests.Mtls;

public sealed class ClientCaTrustProviderTests : IDisposable
{
    private readonly string _dir;

    public ClientCaTrustProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b3-mtls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IOptions<EntryPointListenerOptions> Opts(string bundle, string? denyList = null) =>
        Options.Create(new EntryPointListenerOptions
        {
            Tls = new EntryPointListenerOptions.TlsOptions
            {
                ClientCertificateMode = ClientCertificateMode.Required,
                ClientCa = new EntryPointListenerOptions.ClientCaOptions
                {
                    BundlePath = bundle,
                    DenyListPath = denyList,
                    // Disable the timer in tests; reloads are driven via ReloadNow().
                    ReloadInterval = TimeSpan.Zero,
                },
            },
        });

    [Fact]
    public void Load_MultiCertBundle_ExposesAllAnchors()
    {
        using var ca1 = CertTestUtil.CreateCaCertificate("B3 Bot CA 1");
        using var ca2 = CertTestUtil.CreateCaCertificate("B3 Bot CA 2");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca1, ca2);

        using var provider = new ClientCaTrustProvider(Opts(bundle), NullLogger<ClientCaTrustProvider>.Instance);

        Assert.Equal(2, provider.Current.TrustAnchors.Count);
        Assert.Empty(provider.Current.DeniedThumbprints);
    }

    [Fact]
    public void Load_DenyList_NormalizesAndIgnoresCommentsAndBlanks()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
        var denyPath = Path.Combine(_dir, "deny.txt");
        var tp1 = new string('a', 64);
        var tp2 = string.Concat(Enumerable.Repeat("dd", 32)); // 64 hex
        File.WriteAllLines(denyPath, new[]
        {
            "# a comment",
            "",
            // colon-grouped + mixed case — must canonicalize to tp1
            string.Join(':', Enumerable.Range(0, 32).Select(_ => "AA")),
            "  " + tp2 + "  ",
        });

        using var provider = new ClientCaTrustProvider(
            Opts(bundle, denyPath), NullLogger<ClientCaTrustProvider>.Instance);

        Assert.Equal(2, provider.Current.DeniedThumbprints.Count);
        Assert.True(provider.Current.IsDenied(tp1.ToUpperInvariant()));
        Assert.True(provider.Current.IsDenied(tp2));
        Assert.False(provider.Current.IsDenied(new string('0', 64)));
    }

    [Fact]
    public void Load_DenyList_MalformedEntry_ThrowsAtBoot()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
        var denyPath = Path.Combine(_dir, "deny.txt");
        // 40-char SHA-1-style thumbprint — not a valid SHA-256 (64) entry.
        File.WriteAllText(denyPath, new string('a', 40) + Environment.NewLine);

        var ex = Assert.ThrowsAny<Exception>(() =>
            new ClientCaTrustProvider(Opts(bundle, denyPath), NullLogger<ClientCaTrustProvider>.Instance));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reload_PicksUpNewDenyListEntry_WithoutRestart()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
        var denyPath = Path.Combine(_dir, "deny.txt");
        var tp = string.Concat(Enumerable.Repeat("de", 32)); // 64 hex
        File.WriteAllText(denyPath, string.Empty);

        using var provider = new ClientCaTrustProvider(
            Opts(bundle, denyPath), NullLogger<ClientCaTrustProvider>.Instance);
        Assert.False(provider.Current.IsDenied(tp));

        // Group with colons and lower-case to also exercise normalization.
        File.WriteAllText(denyPath, string.Join(':', Enumerable.Repeat("de", 32)) + Environment.NewLine);
        provider.ReloadNow();

        Assert.True(provider.Current.IsDenied(tp));
    }

    [Fact]
    public void Reload_MalformedEntry_IsLenient_AppliesValidEntries()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
        var denyPath = Path.Combine(_dir, "deny.txt");
        var valid = string.Concat(Enumerable.Repeat("be", 32)); // 64 hex
        File.WriteAllText(denyPath, string.Empty);

        using var provider = new ClientCaTrustProvider(
            Opts(bundle, denyPath), NullLogger<ClientCaTrustProvider>.Instance);

        // One malformed (40-char) line + one valid 64-hex line.
        File.WriteAllLines(denyPath, new[] { new string('a', 40), valid });
        provider.ReloadNow(); // lenient: must not throw

        Assert.True(provider.Current.IsDenied(valid));
        Assert.Single(provider.Current.DeniedThumbprints);
    }

    [Fact]
    public void Load_NonCaCertificateInBundle_ThrowsAtBoot()
    {
        using var leaf = CertTestUtil.CreateLeafCertificate("B3 Not A CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", leaf);

        var ex = Assert.ThrowsAny<Exception>(() =>
            new ClientCaTrustProvider(Opts(bundle), NullLogger<ClientCaTrustProvider>.Instance));
        Assert.Contains("non-CA", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reload_SwapsSnapshotReference_Atomically()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);

        using var provider = new ClientCaTrustProvider(Opts(bundle), NullLogger<ClientCaTrustProvider>.Instance);
        var before = provider.Current;

        provider.ReloadNow();

        Assert.NotSame(before, provider.Current);
    }

    [Fact]
    public void Ctor_MissingBundle_ThrowsAtBoot()
    {
        var missing = Path.Combine(_dir, "nope.pem");
        Assert.ThrowsAny<Exception>(() =>
            new ClientCaTrustProvider(Opts(missing), NullLogger<ClientCaTrustProvider>.Instance));
    }

    [Fact]
    public void Ctor_EmptyBundle_ThrowsAtBoot()
    {
        var empty = Path.Combine(_dir, "empty.pem");
        File.WriteAllText(empty, "not a certificate");
        Assert.ThrowsAny<Exception>(() =>
            new ClientCaTrustProvider(Opts(empty), NullLogger<ClientCaTrustProvider>.Instance));
    }

    [Theory]
    [InlineData("aa:bb:cc", "AABBCC")]
    [InlineData("AA BB CC", "AABBCC")]
    [InlineData("aAbBcC", "AABBCC")]
    [InlineData("  ", "")]
    public void NormalizeThumbprint_StripsSeparatorsAndUppercases(string input, string expected) =>
        Assert.Equal(expected, ClientCaTrustProvider.NormalizeThumbprint(input));
}
