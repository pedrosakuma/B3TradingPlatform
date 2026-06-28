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
