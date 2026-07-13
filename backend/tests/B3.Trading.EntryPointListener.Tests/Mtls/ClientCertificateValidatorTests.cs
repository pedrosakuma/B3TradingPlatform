using System.Security.Cryptography.X509Certificates;
using B3.Trading.EntryPointListener.Mtls;
using static B3.Trading.EntryPointListener.Mtls.ClientCertificateValidator;

namespace B3.Trading.EntryPointListener.Tests.Mtls;

/// <summary>
/// Unit coverage for the pure client-certificate validation logic
/// (RFC user-bot-fixp-mtls-v0 §4.2 / §6). No live TLS socket.
/// </summary>
public sealed class ClientCertificateValidatorTests
{
    private static ClientCaTrustSnapshot Snapshot(
        IEnumerable<X509Certificate2>? anchors = null,
        IEnumerable<string>? denied = null)
    {
        var col = new X509Certificate2Collection();
        if (anchors is not null)
            foreach (var a in anchors) col.Add(a);
        var denySet = new HashSet<string>(
            (denied ?? Enumerable.Empty<string>())
                .Select(ClientCaTrustProvider.NormalizeThumbprint),
            StringComparer.Ordinal);
        return new ClientCaTrustSnapshot(col, denySet, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ModeNone_AlwaysAbsent()
    {
        var outcome = Validate(null, ClientCertificateMode.None, Snapshot(), requireClientAuthEku: true);
        Assert.Equal(Outcome.Absent, outcome);
        Assert.True(outcome.IsAdmitted());
    }

    [Fact]
    public void Required_NoCert_RejectAbsent()
    {
        var outcome = Validate(null, ClientCertificateMode.Required, Snapshot(), true);
        Assert.Equal(Outcome.RejectAbsent, outcome);
        Assert.False(outcome.IsAdmitted());
    }

    [Fact]
    public void Optional_NoCert_AbsentAdmitted()
    {
        var outcome = Validate(null, ClientCertificateMode.Optional, Snapshot(), true);
        Assert.Equal(Outcome.Absent, outcome);
        Assert.True(outcome.IsAdmitted());
    }

    [Fact]
    public void Required_TrustedLeaf_Ok()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        using var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-1");

        var outcome = Validate(leaf, ClientCertificateMode.Required, Snapshot(new[] { ca }), true);

        Assert.Equal(Outcome.Ok, outcome);
    }

    [Fact]
    public void Required_LeafFromUntrustedCa_RejectUntrusted()
    {
        using var trustedCa = CertTestUtil.CreateCaCertificate("B3 Trusted CA");
        using var rogueCa = CertTestUtil.CreateCaCertificate("Rogue CA");
        using var leaf = CertTestUtil.CreateSignedLeaf(rogueCa, "bot-rogue");

        var outcome = Validate(leaf, ClientCertificateMode.Required, Snapshot(new[] { trustedCa }), true);

        Assert.Equal(Outcome.RejectUntrusted, outcome);
    }

    [Fact]
    public void Required_DeniedThumbprint_RejectDenied_EvenIfChainValid()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        using var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-revoked");
        var snapshot = Snapshot(new[] { ca }, new[] { CertTestUtil.Sha256Thumbprint(leaf) });

        var outcome = Validate(leaf, ClientCertificateMode.Required, snapshot, true);

        Assert.Equal(Outcome.RejectDenied, outcome);
    }

    [Fact]
    public void Required_MissingClientAuthEku_RejectWhenRequired()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        using var leaf = CertTestUtil.CreateSignedLeaf(
            ca, "bot-serverauth", clientAuthEku: false, alternateEkuOid: CertTestUtil.ServerAuthOid);

        var outcome = Validate(leaf, ClientCertificateMode.Required, Snapshot(new[] { ca }), requireClientAuthEku: true);

        Assert.Equal(Outcome.RejectNoClientAuthEku, outcome);
    }

    [Fact]
    public void Required_NoEkuExtension_TreatedAsUnrestricted_Ok()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        using var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-no-eku", clientAuthEku: false);

        var outcome = Validate(leaf, ClientCertificateMode.Required, Snapshot(new[] { ca }), requireClientAuthEku: true);

        // No EKU extension at all → unrestricted → accepted.
        Assert.Equal(Outcome.Ok, outcome);
    }

    [Fact]
    public void Required_EkuNotRequired_AcceptsLeafWithoutClientAuth()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        using var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-no-eku", clientAuthEku: false);

        var outcome = Validate(leaf, ClientCertificateMode.Required, Snapshot(new[] { ca }), requireClientAuthEku: false);

        Assert.Equal(Outcome.Ok, outcome);
    }

    [Theory]
    [InlineData(Outcome.Ok, "ok")]
    [InlineData(Outcome.Absent, "absent")]
    [InlineData(Outcome.RejectAbsent, "reject:absent")]
    [InlineData(Outcome.RejectUntrusted, "reject:untrusted")]
    [InlineData(Outcome.RejectDenied, "reject:denied")]
    [InlineData(Outcome.RejectNoClientAuthEku, "reject:no_client_auth_eku")]
    public void ToTag_Mapping(Outcome outcome, string expected) =>
        Assert.Equal(expected, outcome.ToTag());
}
