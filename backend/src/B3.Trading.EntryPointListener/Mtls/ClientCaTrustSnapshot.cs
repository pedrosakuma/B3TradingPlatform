using System.Security.Cryptography.X509Certificates;

namespace B3.Trading.EntryPointListener.Mtls;

/// <summary>
/// Immutable point-in-time view of the client-certificate trust anchors and
/// revocation deny-list (RFC user-bot-fixp-mtls-v0 §4.2 / §5.2). Produced by
/// <see cref="ClientCaTrustProvider"/> and swapped atomically on reload, so a
/// handshake either sees the whole old snapshot or the whole new one — never
/// a torn mix.
/// </summary>
public sealed class ClientCaTrustSnapshot
{
    /// <summary>
    /// The custom trust anchors (issuer CA certificates) a client leaf must
    /// chain to. Used as the <see cref="X509ChainPolicy.CustomTrustStore"/>
    /// under <see cref="X509ChainTrustMode.CustomRootTrust"/> — the OS root
    /// store is deliberately never consulted (RFC §4.2 / §10.2).
    /// </summary>
    public X509Certificate2Collection TrustAnchors { get; }

    /// <summary>
    /// Normalized (upper-case hex, no separators) SHA-256 leaf thumbprints
    /// that are denied even when their chain validates — the network-free
    /// fast revocation path (RFC §4.4).
    /// </summary>
    public IReadOnlySet<string> DeniedThumbprints { get; }

    /// <summary>UTC instant this snapshot was loaded.</summary>
    public DateTimeOffset LoadedAtUtc { get; }

    public ClientCaTrustSnapshot(
        X509Certificate2Collection trustAnchors,
        IReadOnlySet<string> deniedThumbprints,
        DateTimeOffset loadedAtUtc)
    {
        TrustAnchors = trustAnchors;
        DeniedThumbprints = deniedThumbprints;
        LoadedAtUtc = loadedAtUtc;
    }

    /// <summary>True when <paramref name="thumbprint"/> (any casing/separators)
    /// is on the deny-list.</summary>
    public bool IsDenied(string? thumbprint) =>
        thumbprint is not null &&
        DeniedThumbprints.Contains(ClientCaTrustProvider.NormalizeThumbprint(thumbprint));
}
