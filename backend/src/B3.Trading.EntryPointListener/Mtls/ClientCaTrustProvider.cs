using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Mtls;

/// <summary>
/// Hot-reloading provider of the client-certificate trust snapshot
/// (RFC user-bot-fixp-mtls-v0 §5.2). The CA bundle and deny-list are read
/// eagerly at construction (so <see cref="Current"/> is valid before the
/// listener accepts a single connection) and re-read on a timer at
/// <see cref="EntryPointListenerOptions.ClientCaOptions.ReloadInterval"/>.
///
/// <para>The reload swaps the whole <see cref="ClientCaTrustSnapshot"/>
/// atomically (<see cref="Volatile"/> reference write), so a handshake reading
/// <see cref="Current"/> never observes a torn mix. A <em>reload</em> failure
/// (file briefly unreadable mid-write, transient parse error) is logged and the
/// previous good snapshot is retained — the listener fails safe, not closed,
/// on a flaky filesystem. The <em>initial</em> load throws so a fundamentally
/// misconfigured bundle fails the host at boot (the validator also guards this
/// up front).</para>
/// </summary>
public sealed class ClientCaTrustProvider : IClientCaTrustProvider, IDisposable
{
    private readonly EntryPointListenerOptions.ClientCaOptions _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClientCaTrustProvider> _logger;
    private readonly ITimer? _timer;

    private ClientCaTrustSnapshot _current;

    /// <summary>A SHA-256 thumbprint is 32 bytes = 64 hex characters.</summary>
    private const int Sha256HexLength = 64;

    public ClientCaTrustProvider(
        IOptions<EntryPointListenerOptions> options,
        ILogger<ClientCaTrustProvider> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _opts = options.Value.Tls.ClientCa;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;

        // Eager initial load — fail closed at boot on a broken bundle.
        _current = Load(_opts, _clock, strict: true);
        _logger.LogInformation(
            "fixp.mtls.ca.loaded anchors={Anchors} denied={Denied} bundle={Bundle}",
            _current.TrustAnchors.Count, _current.DeniedThumbprints.Count, _opts.BundlePath);

        var interval = _opts.ReloadInterval;
        if (interval > TimeSpan.Zero)
            _timer = _clock.CreateTimer(_ => SafeReload(), null, interval, interval);
    }

    /// <inheritdoc />
    public ClientCaTrustSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Re-reads the bundle + deny-list and atomically swaps the snapshot.
    /// Runtime reloads are <em>lenient</em>: a malformed deny-list line is
    /// skipped (with a warning) rather than throwing, so a freshly-added
    /// valid revocation in the same file still takes effect. Exposed
    /// <c>internal</c> so tests can trigger a deterministic reload without
    /// waiting on the timer.
    /// </summary>
    internal void ReloadNow()
    {
        var next = Load(_opts, _clock, strict: false);
        Volatile.Write(ref _current, next);
        _logger.LogInformation(
            "fixp.mtls.ca.reloaded anchors={Anchors} denied={Denied}",
            next.TrustAnchors.Count, next.DeniedThumbprints.Count);
    }

    private void SafeReload()
    {
        try
        {
            ReloadNow();
        }
        catch (Exception ex)
        {
            // Keep the previous good snapshot — never fail open or crash the
            // listener because a file was unreadable mid-rotation.
            _logger.LogWarning(ex,
                "fixp.mtls.ca.reload_failed bundle={Bundle}; retaining previous snapshot.",
                _opts.BundlePath);
        }
    }

    private ClientCaTrustSnapshot Load(
        EntryPointListenerOptions.ClientCaOptions opts, TimeProvider clock, bool strict)
    {
        var anchors = LoadTrustAnchors(opts.BundlePath);
        var denied = LoadDenyList(opts.DenyListPath, out var invalidLines);
        if (invalidLines > 0)
        {
            if (strict)
                throw new InvalidOperationException(
                    $"Client-CA deny-list '{opts.DenyListPath}' has {invalidLines} malformed " +
                    "entr(y/ies); each entry must be a 64-character SHA-256 hex thumbprint.");

            _logger.LogWarning(
                "fixp.mtls.ca.denylist_malformed skipped={Skipped} bundle={Bundle}",
                invalidLines, opts.BundlePath);
        }

        return new ClientCaTrustSnapshot(anchors, denied, clock.GetUtcNow());
    }

    /// <summary>
    /// Loads the PEM trust-anchor bundle. Throws when the path is unset,
    /// missing, contains no parseable certificate, or contains a leaf that is
    /// not a CA (RFC §4.2: every anchor must be an issuer CA — a non-CA cert
    /// in a <see cref="X509ChainTrustMode.CustomRootTrust"/> store would
    /// otherwise be honoured as a self-signed anchor). Shared with the options
    /// validator so boot-time and runtime loading agree exactly.
    /// </summary>
    internal static X509Certificate2Collection LoadTrustAnchors(string? bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
            throw new InvalidOperationException("Client-CA bundle path is not set.");
        if (!File.Exists(bundlePath))
            throw new FileNotFoundException($"Client-CA bundle '{bundlePath}' does not exist.", bundlePath);

        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(bundlePath);
        if (collection.Count == 0)
            throw new InvalidOperationException(
                $"Client-CA bundle '{bundlePath}' contains no PEM certificate.");

        foreach (var cert in collection)
        {
            if (!IsCertificateAuthority(cert))
                throw new InvalidOperationException(
                    $"Client-CA bundle '{bundlePath}' contains a non-CA certificate " +
                    $"(subject '{cert.Subject}'). Trust anchors must have BasicConstraints CA=true.");
        }

        return collection;
    }

    /// <summary>
    /// True when <paramref name="cert"/> is usable as a CA trust anchor:
    /// BasicConstraints marks it a CA, and — when a KeyUsage extension is
    /// present — it permits certificate signing. A cert with no KeyUsage
    /// extension is unrestricted and therefore allowed.
    /// </summary>
    private static bool IsCertificateAuthority(X509Certificate2 cert)
    {
        var isCa = false;
        var keyUsageOk = true;
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509BasicConstraintsExtension bc)
                isCa = bc.CertificateAuthority;
            else if (ext is X509KeyUsageExtension ku)
                keyUsageOk = (ku.KeyUsages & X509KeyUsageFlags.KeyCertSign) != 0;
        }

        return isCa && keyUsageOk;
    }

    /// <summary>
    /// Loads the deny-list into a normalized set. A null/empty path yields an
    /// empty set. Blank lines and <c>#</c>-prefixed comment lines are ignored.
    /// Every other line must canonicalize (via <see cref="NormalizeThumbprint"/>)
    /// to exactly 64 hex characters — a SHA-256 leaf thumbprint; entries that
    /// do not are counted in <paramref name="invalidLineCount"/> and excluded
    /// so a typo or stale SHA-1 value cannot masquerade as an effective
    /// revocation.
    /// </summary>
    internal static IReadOnlySet<string> LoadDenyList(string? denyListPath, out int invalidLineCount)
    {
        invalidLineCount = 0;
        if (string.IsNullOrWhiteSpace(denyListPath))
            return new HashSet<string>();
        if (!File.Exists(denyListPath))
            throw new FileNotFoundException(
                $"Client-CA deny-list '{denyListPath}' does not exist.", denyListPath);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(denyListPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var normalized = NormalizeThumbprint(line);
            if (normalized.Length == Sha256HexLength)
                set.Add(normalized);
            else
                invalidLineCount++;
        }

        return set;
    }

    /// <summary>
    /// Canonicalizes a certificate thumbprint to upper-case hex with all
    /// separators (colons, spaces, tabs) stripped, so deny-list entries and
    /// computed leaf thumbprints compare regardless of formatting.
    /// </summary>
    public static string NormalizeThumbprint(string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);
        var sb = new StringBuilder(thumbprint.Length);
        foreach (var ch in thumbprint)
        {
            if (Uri.IsHexDigit(ch))
                sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    public void Dispose() => _timer?.Dispose();
}
