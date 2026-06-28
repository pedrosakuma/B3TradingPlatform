using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace B3.Trading.EntryPointListener.Tests.Mtls;

/// <summary>
/// Test helpers for minting throw-away X509 certificates and writing PEM
/// bundles / deny-lists to a temp directory the test owns and cleans up.
/// </summary>
internal static class CertTestUtil
{
    public static X509Certificate2 CreateCaCertificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
    }

    /// <summary>A self-signed end-entity (non-CA) certificate — BasicConstraints CA=false.</summary>
    public static X509Certificate2 CreateLeafCertificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, true));
        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    /// <summary>
    /// Creates an end-entity leaf signed by <paramref name="issuer"/> (a CA
    /// from <see cref="CreateCaCertificate"/>). The returned cert carries its
    /// private key so it can be presented by a TLS client.
    /// </summary>
    public static X509Certificate2 CreateSignedLeaf(
        X509Certificate2 issuer,
        string commonName,
        bool clientAuthEku = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string? alternateEkuOid = null)
    {
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, true));
        if (clientAuthEku)
        {
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(ClientAuthOid) }, critical: false));
        }
        else if (alternateEkuOid is not null)
        {
            // A present-but-not-clientAuth EKU (e.g. serverAuth) so the
            // validator's "EKU listed but missing clientAuth" path is exercised.
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(alternateEkuOid) }, critical: false));
        }

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        var now = DateTimeOffset.UtcNow;
        using var signed = req.Create(
            issuer, notBefore ?? now.AddDays(-1), notAfter ?? now.AddYears(1), serial);
        return signed.CopyWithPrivateKey(rsa);
    }

    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public static string WritePemBundle(string dir, string fileName, params X509Certificate2[] certs)
    {
        var path = Path.Combine(dir, fileName);
        var pem = string.Join(
            Environment.NewLine,
            certs.Select(c => c.ExportCertificatePem()));
        File.WriteAllText(path, pem);
        return path;
    }

    /// <summary>Computes the upper-case-hex SHA-256 thumbprint of a cert.</summary>
    public static string Sha256Thumbprint(X509Certificate2 cert) =>
        cert.GetCertHashString(HashAlgorithmName.SHA256);
}
