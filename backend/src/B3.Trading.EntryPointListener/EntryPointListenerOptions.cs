namespace B3.Trading.EntryPointListener;

/// <summary>
/// Client-certificate (mTLS) enforcement mode for the FIXP listener
/// (RFC user-bot-fixp-mtls-v0 §5). Defaults to <see cref="None"/> so the
/// historical server-only-TLS behaviour is preserved.
/// </summary>
public enum ClientCertificateMode
{
    /// <summary>No client certificate requested. Bot identity rests on the
    /// PAT alone (today's behaviour).</summary>
    None = 0,

    /// <summary>Client certificate requested and validated <em>if presented</em>,
    /// but a connection without one is still admitted — observe-then-enforce
    /// rollout. Pinned credentials enforce the thumbprint only when a cert
    /// was presented.</summary>
    Optional = 1,

    /// <summary>Client certificate required: a connection without a valid,
    /// trusted certificate is rejected during the TLS handshake before any
    /// application bytes are processed.</summary>
    Required = 2,
}

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
    /// RFC user-bot-fixp-mtls-v0 §7. Explicit opt-in escape hatch that
    /// permits a <em>less secure than default public posture</em> mTLS
    /// configuration in Production — e.g. running
    /// <see cref="ClientCertificateMode.Required"/> without a configured
    /// deny-list, or silencing the loud boot warning emitted when mTLS is
    /// <see cref="ClientCertificateMode.None"/>/<see cref="ClientCertificateMode.Optional"/>
    /// in Production. Mirrors the <see cref="AllowInProduction"/> /
    /// <c>AllowErInjectionInProduction</c> opt-in shape so a weaker posture
    /// is always an explicit, audited choice. Consumed by the boot guard
    /// (sub-issue E). Default false.
    /// </summary>
    public bool AllowInsecureMtlsInProduction { get; set; }

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

    /// <summary>
    /// RFC user-bot-fixp-mtls-v0 §10.5. Pre-Negotiate accept-loop
    /// connection-rate limit, the only knob that bounds the TLS-handshake
    /// flood vector (the per-IP <see cref="RateLimit"/> only applies after
    /// Negotiate). Opt-in: default disabled — public deployments are
    /// expected to front the listener with an LB/WAF connection-rate cap.
    /// </summary>
    public AcceptRateLimitOptions AcceptRateLimit { get; set; } = new();

    /// <summary>
    /// Public-grade abuse hardening (#529): pre-auth concurrent-connection
    /// caps + optional source-IP allow/deny. Opt-in; defaults are
    /// permissive (0 = unlimited, empty = no filter) so UAT behaviour is
    /// unchanged. Tighten via env for hostile-internet exposure.
    /// </summary>
    public ConnectionCapsOptions ConnectionCaps { get; set; } = new();

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

        /// <summary>
        /// RFC user-bot-fixp-mtls-v0 §5. Client-certificate (mTLS)
        /// enforcement mode. Meaningful only when <see cref="Required"/>
        /// is true (you cannot do mTLS without TLS). Default
        /// <see cref="ClientCertificateMode.None"/> preserves today's
        /// server-only-TLS behaviour.
        /// </summary>
        public ClientCertificateMode ClientCertificateMode { get; set; } = ClientCertificateMode.None;

        /// <summary>
        /// RFC user-bot-fixp-mtls-v0 §4.2 / §5. Trust-anchor and
        /// revocation configuration for client-certificate validation.
        /// Required when <see cref="ClientCertificateMode"/> is not
        /// <see cref="ClientCertificateMode.None"/>.
        /// </summary>
        public ClientCaOptions ClientCa { get; set; } = new();

        /// <summary>
        /// RFC user-bot-fixp-mtls-v0 §4.1 / §7. When true, the client-cert
        /// validation callback requires the leaf to carry the
        /// <c>clientAuth</c> Enhanced Key Usage (1.3.6.1.5.5.7.3.2).
        /// Default true. Consumed by the handshake gate (sub-issue C).
        /// </summary>
        public bool RequireClientAuthEku { get; set; } = true;

        /// <summary>Returns true when <see cref="CertPath"/> ends in a PFX/P12 extension.</summary>
        public bool IsPfx => CertPath is not null &&
            (CertPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
             CertPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase));

        /// <summary>True when client-certificate (mTLS) enforcement is active.</summary>
        public bool MtlsEnabled => ClientCertificateMode != ClientCertificateMode.None;

        /// <summary>
        /// Public-hardening (#529). Maximum time the TLS handshake may take
        /// before the socket is dropped — bounds slow-loris handshake
        /// exhaustion. Default 5 s. Must be &gt; <see cref="TimeSpan.Zero"/>.
        /// </summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// RFC user-bot-fixp-mtls-v0 §4.2 / §5.2. Trust-anchor (custom-root)
    /// and revocation inputs for client-certificate validation. Both files
    /// are hot-reloaded by the trust provider so a CA rotation or a
    /// thumbprint revocation takes effect without restarting the listener.
    /// </summary>
    public sealed class ClientCaOptions
    {
        /// <summary>
        /// Path to a PEM bundle (one or more concatenated
        /// <c>-----BEGIN CERTIFICATE-----</c> blocks) of the issuer CA(s)
        /// that are trusted to sign bot client certificates. This is the
        /// <em>custom</em> trust anchor — the OS root store is never
        /// consulted (RFC §4.2). Required when mTLS is enabled.
        /// </summary>
        public string? BundlePath { get; set; }

        /// <summary>
        /// Optional path to a newline-delimited list of SHA-256 leaf
        /// thumbprints (hex, separators and <c>#</c> comment lines ignored)
        /// that are denied even when their chain is otherwise valid — the
        /// network-free fast revocation path (RFC §4.4).
        /// </summary>
        public string? DenyListPath { get; set; }

        /// <summary>
        /// Cadence at which <see cref="BundlePath"/> and
        /// <see cref="DenyListPath"/> are re-read and atomically swapped
        /// (RFC §5.2). Default 5 minutes. Must be &gt; <see cref="TimeSpan.Zero"/>.
        /// </summary>
        public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// RFC user-bot-fixp-mtls-v0 §10.5. Pre-Negotiate accept-loop
    /// connection-rate limit. Disabled by default
    /// (<see cref="ConnectionsPerSecondPerIp"/> = 0): public exposure is
    /// expected to be fronted by an LB/WAF. When enabled, a token-bucket
    /// per source IP throttles new TCP connections before the TLS
    /// handshake runs, bounding the handshake-flood DoS vector.
    /// </summary>
    public sealed class AcceptRateLimitOptions
    {
        /// <summary>
        /// Steady-state accepted connections per second per source IP.
        /// 0 disables the accept-loop limit entirely. Default 0.
        /// </summary>
        public int ConnectionsPerSecondPerIp { get; set; }

        /// <summary>Token-bucket burst capacity per source IP. Default 30.</summary>
        public int BurstPerIp { get; set; } = 30;
    }

    /// <summary>
    /// #529 public-grade abuse hardening. Pre-auth concurrent-connection
    /// caps and source-IP access control evaluated in the accept loop
    /// before any TLS/Negotiate bytes. All defaults are permissive so UAT
    /// posture is unchanged; tighten via env for public exposure.
    /// </summary>
    public sealed class ConnectionCapsOptions
    {
        /// <summary>
        /// Maximum live connections across all peers. 0 = unlimited.
        /// New accepts beyond the cap are closed immediately. Default 0.
        /// </summary>
        public int MaxConcurrentTotal { get; set; }

        /// <summary>
        /// Maximum live connections per source IP. 0 = unlimited. Caps
        /// pre-Negotiate slow-loris fan-out from a single host. Default 0.
        /// </summary>
        public int MaxConcurrentPerIp { get; set; }

        /// <summary>
        /// If non-empty, only these source IPs may connect (allow-list
        /// wins; every other IP is rejected). Exact-match IPv4/IPv6.
        /// </summary>
        public IList<string> AllowedIps { get; set; } = new List<string>();

        /// <summary>
        /// Source IPs rejected before handshake. Ignored for an IP that is
        /// also in <see cref="AllowedIps"/> (allow-list takes precedence).
        /// </summary>
        public IList<string> DeniedIps { get; set; } = new List<string>();
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
