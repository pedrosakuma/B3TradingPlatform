namespace B3.Trading.EntryPointListener;

/// <summary>
/// Configuration for the inbound FIXP/SBE listener that lets external bots
/// connect to the trading-host using native B3 EntryPoint protocol.
/// </summary>
public sealed class EntryPointListenerOptions
{
    public const string SectionName = "Trading:EntryPointListener";

    /// <summary>Whether the FIXP listener is enabled at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Bind address in <c>host:port</c> form. Use <c>*:port</c> or
    /// <c>0.0.0.0:port</c> to bind on all interfaces. Port 0 lets the OS
    /// pick a free port (useful in tests).
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>TLS configuration for the listener socket.</summary>
    public TlsOptions Tls { get; set; } = new();

    /// <summary>
    /// Explicit opt-in required for <c>Environment=Production</c>.
    /// Must be combined with <see cref="TlsOptions.Required"/>=true and
    /// valid cert/key paths to satisfy the boot guard.
    /// </summary>
    public bool AllowInProduction { get; set; }

    /// <summary>Nested TLS configuration.</summary>
    public sealed class TlsOptions
    {
        /// <summary>Path to the PEM certificate file.</summary>
        public string? CertPath { get; set; }

        /// <summary>Path to the PEM private-key file.</summary>
        public string? KeyPath { get; set; }

        /// <summary>
        /// When true the host refuses to serve unencrypted sessions.
        /// TLS in-socket is deferred to sub-issue E; the boot guard
        /// enforces this flag but no <c>SslStream</c> wrapping occurs yet.
        /// </summary>
        public bool Required { get; set; }
    }
}
