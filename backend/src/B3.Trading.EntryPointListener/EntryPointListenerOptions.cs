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

    /// <summary>
    /// Sub-issue #173 (G). Cadence (ms) at which an established
    /// connection emits <c>Sequence</c> as a server→bot heartbeat / gap
    /// signal. Suppressed when a real outbound message was sent within
    /// the window (piggyback semantics, RFC §4.7). Default 3000 ms.
    /// Set ≤0 to disable.
    /// </summary>
    public int HeartbeatIntervalMs { get; set; } = 3000;

    /// <summary>
    /// Sub-issue #173 (G). Maximum bot inbound message-rate window the
    /// listener will tolerate gaps within before considering the gap
    /// permanent. Currently advisory — v0 does not auto-Terminate on
    /// unfilled gaps (idempotent flow: bot reconciles via REST instead);
    /// surfaced for forward compatibility with H's hardening pass.
    /// </summary>
    public int RetransmitTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Maximum number of concurrent FIXP sessions a single user may hold
    /// across all their credentials. A 4th session for the same userId is
    /// rejected with <c>NegotiateReject(CREDENTIALS)</c>.
    /// </summary>
    public int MaxSessionsPerUser { get; set; } = 3;

    /// <summary>Rate-limit options for Negotiate requests.</summary>
    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>Outbound buffer sizing options.</summary>
    public BuffersOptions Buffers { get; set; } = new();

    /// <summary>
    /// TCP socket tunables applied to every accepted client (RFC §5.9 / P11).
    /// </summary>
    public FixpTcpOptions Tcp { get; set; } = new();

    /// <summary>Nested TLS configuration.</summary>
    public sealed class TlsOptions
    {
        /// <summary>
        /// Path to the certificate file. PEM (<c>.crt</c>/<c>.pem</c>) or
        /// PFX/PKCS#12 (<c>.pfx</c>/<c>.p12</c>). When using PFX, leave
        /// <see cref="KeyPath"/> empty — the private key is inside the PFX.
        /// </summary>
        public string? CertPath { get; set; }

        /// <summary>Path to the PEM private-key file. Required for PEM certs, optional for PFX.</summary>
        public string? KeyPath { get; set; }

        /// <summary>
        /// When true the host refuses to serve unencrypted sessions and wraps
        /// accepted connections in <see cref="System.Net.Security.SslStream"/>.
        /// </summary>
        public bool Required { get; set; }

        /// <summary>
        /// Optional passphrase for an encrypted PEM private key or PFX file.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>Returns true when <see cref="CertPath"/> ends in a PFX/P12 extension.</summary>
        public bool IsPfx => CertPath is not null &&
            (CertPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
             CertPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Token-bucket rate limiting for Negotiate requests.</summary>
    public sealed class RateLimitOptions
    {
        /// <summary>Max Negotiate requests per minute per source IP.</summary>
        public int NegotiatesPerMinutePerIp { get; set; } = 30;

        /// <summary>
        /// Max Negotiate requests per minute per credential identity (applied
        /// after credential lookup succeeds, keyed by CredentialId).
        /// </summary>
        public int NegotiatesPerMinutePerUsername { get; set; } = 10;
    }

    /// <summary>Outbound buffer sizing.</summary>
    public sealed class BuffersOptions
    {
        /// <summary>Outbound ring buffer size (entries).</summary>
        public int OutboundRingSize { get; set; } = 1024;

        /// <summary>Idle bot-mapping reap interval.</summary>
        public TimeSpan MappingReapAfter { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// RFC §5.3 / P8 / F3. Capacity of the per-FIXP-connection
        /// bounded outbound channel that the drain loop reads from.
        /// When the channel fills up, <c>TryEnqueue</c> returns false
        /// (RFC §5.3.1 backpressure: surface, never silently drop) —
        /// the message stays owned by the per-credential
        /// <see cref="UserBots.BotOutboundBuffer"/> and is replayed via
        /// retransmit on the next reconnect. Default 4096 — sized to
        /// absorb a transient socket-write stall of several seconds
        /// at typical ER rates without surfacing backpressure.
        /// </summary>
        public int OutboundChannelCapacity { get; set; } = 4096;

        /// <summary>
        /// RFC §5.3.2 / P8 / F3. Maximum time the per-connection drain
        /// loop will spend flushing already-queued outbound frames on
        /// connection close before giving up and returning. Frames
        /// still queued when the deadline elapses remain owned by the
        /// per-credential <see cref="UserBots.BotOutboundBuffer"/> and
        /// are replayed via retransmit on the next reconnect — they
        /// are NEVER silently dropped from the bot's perspective.
        /// Default 1s.
        /// </summary>
        public TimeSpan OutboundDrainShutdownTimeout { get; set; } = TimeSpan.FromSeconds(1);
    }
}
