using System.Security.Cryptography.X509Certificates;

namespace B3.Trading.EntryPointListener.Mtls;

/// <summary>
/// Pure client-certificate validation logic for the FIXP TLS handshake gate
/// (RFC user-bot-fixp-mtls-v0 §4.2 / §6). Factored out of the hosted service
/// so the chain-building, deny-list, EKU, and mode semantics are unit-testable
/// without a live TLS socket.
/// </summary>
public static class ClientCertificateValidator
{
    /// <summary>The <c>clientAuth</c> Enhanced Key Usage OID (RFC 5280).</summary>
    public const string ClientAuthEku = "1.3.6.1.5.5.7.3.2";

    /// <summary>Outcome of validating (or skipping) a presented client cert.</summary>
    public enum Outcome
    {
        /// <summary>A valid, trusted client certificate was presented.</summary>
        Ok,

        /// <summary>No certificate was presented and the mode permits it (Optional).</summary>
        Absent,

        /// <summary>No certificate was presented but the mode requires one (Required).</summary>
        RejectAbsent,

        /// <summary>The certificate did not chain to the configured trust anchor.</summary>
        RejectUntrusted,

        /// <summary>The certificate's SHA-256 thumbprint is on the deny-list.</summary>
        RejectDenied,

        /// <summary>The certificate lacks the required <c>clientAuth</c> EKU.</summary>
        RejectNoClientAuthEku,
    }

    /// <summary>True when <paramref name="outcome"/> means the connection is admitted.</summary>
    public static bool IsAdmitted(this Outcome outcome) =>
        outcome is Outcome.Ok or Outcome.Absent;

    /// <summary>Lower-case reason tag for metrics / logs (e.g. <c>reject:untrusted</c>).</summary>
    public static string ToTag(this Outcome outcome) => outcome switch
    {
        Outcome.Ok => "ok",
        Outcome.Absent => "absent",
        Outcome.RejectAbsent => "reject:absent",
        Outcome.RejectUntrusted => "reject:untrusted",
        Outcome.RejectDenied => "reject:denied",
        Outcome.RejectNoClientAuthEku => "reject:no_client_auth_eku",
        _ => "reject:unknown",
    };

    /// <summary>
    /// Validates the presented client certificate against the snapshot and mode.
    /// Builds the chain under <see cref="X509ChainTrustMode.CustomRootTrust"/>
    /// against the configured anchors only — the OS root store is never
    /// consulted (RFC §4.2 / §10.2).
    /// </summary>
    /// <param name="certificate">The presented leaf, or null when none was sent.</param>
    /// <param name="mode">The configured enforcement mode.</param>
    /// <param name="snapshot">Current trust anchors + deny-list.</param>
    /// <param name="requireClientAuthEku">Whether to require the clientAuth EKU.</param>
    /// <param name="presentedChain">
    /// The chain the TLS layer built from the certificates the peer sent. Its
    /// intermediates are fed into the offline <c>ExtraStore</c> so legitimate
    /// multi-level chains still build without any network fetch.
    /// </param>
    public static Outcome Validate(
        X509Certificate2? certificate,
        ClientCertificateMode mode,
        ClientCaTrustSnapshot snapshot,
        bool requireClientAuthEku,
        X509Chain? presentedChain = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (mode == ClientCertificateMode.None)
            return Outcome.Absent;

        if (certificate is null)
            return mode == ClientCertificateMode.Required ? Outcome.RejectAbsent : Outcome.Absent;

        // Deny-list first: a revoked thumbprint is rejected even if the chain
        // would otherwise validate (RFC §4.4).
        var thumbprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (snapshot.IsDenied(thumbprint))
            return Outcome.RejectDenied;

        if (requireClientAuthEku && !HasClientAuthEku(certificate))
            return Outcome.RejectNoClientAuthEku;

        return ChainsToTrustAnchor(certificate, snapshot.TrustAnchors, presentedChain)
            ? Outcome.Ok
            : Outcome.RejectUntrusted;
    }

    private static bool ChainsToTrustAnchor(
        X509Certificate2 leaf, X509Certificate2Collection anchors, X509Chain? presentedChain)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        // Never let chain building reach out to AIA URLs embedded in an
        // attacker-supplied cert: that would turn the handshake callback into a
        // synchronous outbound fetch / DoS vector (RFC §10.2). Anchors come
        // only from the configured trust store; intermediates only from what
        // the peer already presented.
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.CustomTrustStore.AddRange(anchors);

        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        return chain.Build(leaf);
    }

    private static bool HasClientAuthEku(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509EnhancedKeyUsageExtension eku)
            {
                foreach (var oid in eku.EnhancedKeyUsages)
                {
                    if (oid.Value == ClientAuthEku)
                        return true;
                }

                // EKU extension present but does not list clientAuth.
                return false;
            }
        }

        // No EKU extension at all → unrestricted → acceptable.
        return true;
    }
}
