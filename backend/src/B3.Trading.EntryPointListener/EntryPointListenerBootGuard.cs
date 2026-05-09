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

        return "⚠ FIXP LISTENER ENABLED on " + opts.Endpoint + "." + tlsNote + prodNote;
    }
}
