using B3.Trading.EntryPointListener.Mtls;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.EntryPointListener;

/// <summary>
/// Production safeguard for the inbound FIXP listener.  Mirrors the shape
/// of <c>ErInjectionBootGuard</c>: pure static, no DI dependencies,
/// unit-testable in isolation.
///
/// <para>
/// The listener has catastrophic blast radius if exposed in Production
/// without TLS — any network client could send orders as any platform user.
/// Therefore the host refuses to boot when
/// <c>Enabled=true + Environment=Production</c> unless the operator has
/// explicitly opted in via <see cref="EntryPointListenerOptions.AllowInProduction"/>
/// AND has configured TLS (<see cref="EntryPointListenerOptions.TlsOptions.Required"/>
/// plus valid cert/key paths).
/// </para>
/// </summary>
public static class EntryPointListenerBootGuard
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the listener is
    /// enabled in Production without the full safety set.  No-op when
    /// disabled or when the environment is not Production.
    /// </summary>
    public static void Validate(string environmentName, EntryPointListenerOptions opts)
    {
        if (!opts.Enabled) return;

        var isProduction = string.Equals(
            environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);

        if (!isProduction) return;

        if (!opts.AllowInProduction)
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Enabled=true is not allowed in Production without " +
                "Trading:EntryPointListener:AllowInProduction=true. " +
                "Additionally, Tls.Required=true and valid Tls.CertPath/KeyPath are required.");
        }

        if (!opts.Tls.Required)
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Enabled=true in Production requires " +
                "Trading:EntryPointListener:Tls:Required=true. " +
                "Serving unencrypted FIXP sessions in Production is not permitted.");
        }

        if (string.IsNullOrWhiteSpace(opts.Tls.CertPath))
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Enabled=true in Production requires non-empty " +
                "Trading:EntryPointListener:Tls:CertPath.");
        }

        if (!opts.Tls.IsPfx && string.IsNullOrWhiteSpace(opts.Tls.KeyPath))
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Enabled=true in Production requires non-empty " +
                "Trading:EntryPointListener:Tls:KeyPath (or use a .pfx/.p12 CertPath).");
        }

        ValidateMtls(opts);
    }

    /// <summary>
    /// Production mTLS rules (RFC §7). <see cref="ClientCertificateMode.None"/>
    /// and <see cref="ClientCertificateMode.Optional"/> warn (via
    /// <see cref="BuildWarning"/>) but boot. <see cref="ClientCertificateMode.Required"/>
    /// fails closed unless its trust material is fully configured: a parseable
    /// CA bundle, plus a deny-list (revocation) — the latter is waivable only
    /// with the explicit <see cref="EntryPointListenerOptions.AllowInsecureMtlsInProduction"/>
    /// opt-in. Assumes the caller has already established that the environment
    /// is Production.
    /// </summary>
    private static void ValidateMtls(EntryPointListenerOptions opts)
    {
        if (opts.Tls.ClientCertificateMode != ClientCertificateMode.Required)
            return;

        var bundlePath = opts.Tls.ClientCa.BundlePath;
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Tls:ClientCertificateMode=Required in Production requires " +
                "a non-empty Trading:EntryPointListener:Tls:ClientCa:BundlePath.");
        }

        int anchorCount;
        try
        {
            var anchors = ClientCaTrustProvider.LoadTrustAnchors(bundlePath);
            anchorCount = anchors.Count;
            foreach (var anchor in anchors)
                anchor.Dispose();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Tls:ClientCa:BundlePath '" + bundlePath +
                "' could not be parsed as a CA bundle: " + ex.Message, ex);
        }

        if (anchorCount == 0)
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Tls:ClientCa:BundlePath '" + bundlePath +
                "' contained no CA certificates; mTLS Required cannot trust any client.");
        }

        if (string.IsNullOrWhiteSpace(opts.Tls.ClientCa.DenyListPath) &&
            !opts.AllowInsecureMtlsInProduction)
        {
            throw new InvalidOperationException(
                "Trading:EntryPointListener:Tls:ClientCertificateMode=Required in Production requires " +
                "a Trading:EntryPointListener:Tls:ClientCa:DenyListPath for revocation. " +
                "Set Trading:EntryPointListener:AllowInsecureMtlsInProduction=true to run without one.");
        }
    }

    /// <summary>
    /// Returns a multi-line boot-time warning banner when the listener is
    /// active. Returns <c>null</c> when disabled so the caller can skip
    /// the log call entirely.
    /// </summary>
    public static string? BuildWarning(string environmentName, EntryPointListenerOptions opts)
    {
        if (!opts.Enabled) return null;

        var isProduction = string.Equals(
            environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);

        var tlsNote = opts.Tls.Required
            ? " TLS.Required=true — connections wrapped in SslStream."
            : " ⚠ TLS.Required=false — listener is serving PLAINTEXT. Do NOT use in Production.";

        var prodNote = isProduction
            ? " ‼ PRODUCTION ENVIRONMENT — AllowInProduction=true is set. Verify TLS configuration."
            : string.Empty;

        return "⚠ FIXP LISTENER ENABLED on " + opts.Endpoint + "." + tlsNote + prodNote +
            BuildMtlsNote(opts, isProduction);
    }

    /// <summary>
    /// The mTLS fragment of the boot banner (RFC §7.4). In Production, an
    /// insecure posture (<see cref="ClientCertificateMode.None"/>/<see
    /// cref="ClientCertificateMode.Optional"/>) renders loudly unless the
    /// operator has explicitly accepted it via
    /// <see cref="EntryPointListenerOptions.AllowInsecureMtlsInProduction"/>.
    /// </summary>
    private static string BuildMtlsNote(EntryPointListenerOptions opts, bool isProduction)
    {
        var mode = opts.Tls.ClientCertificateMode;
        var loudInProd = isProduction && !opts.AllowInsecureMtlsInProduction;

        if (mode == ClientCertificateMode.None)
        {
            return loudInProd
                ? " ⚠ mTLS: None — bot identity rests on PAT alone."
                : " mTLS: None.";
        }

        var bundle = string.IsNullOrWhiteSpace(opts.Tls.ClientCa.BundlePath)
            ? "(none)"
            : opts.Tls.ClientCa.BundlePath;
        var denyNote = DescribeDenyList(opts.Tls.ClientCa.DenyListPath);

        if (mode == ClientCertificateMode.Optional && loudInProd)
        {
            return " ⚠ mTLS: Optional (CA bundle: " + bundle + ", deny-list: " + denyNote +
                ") — clients may connect WITHOUT a certificate.";
        }

        return " mTLS: " + mode + " (CA bundle: " + bundle + ", deny-list: " + denyNote + ").";
    }

    private static string DescribeDenyList(string? denyListPath)
    {
        if (string.IsNullOrWhiteSpace(denyListPath))
            return "none";

        try
        {
            var denied = ClientCaTrustProvider.LoadDenyList(denyListPath, out _);
            return denied.Count + " entries";
        }
        catch
        {
            return "unreadable";
        }
    }
}
