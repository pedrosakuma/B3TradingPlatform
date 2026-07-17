using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Mtls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// <see cref="BackgroundService"/> that binds a <see cref="TcpListener"/>,
/// accepts inbound FIXP connections, and dispatches each to a new
/// <see cref="FixpSessionConnection"/> running on the thread-pool.
///
/// <para>The service is only registered when
/// <c>Trading:EntryPointListener:Enabled=true</c>.</para>
///
/// <para>When <see cref="EntryPointListenerOptions.TlsOptions.Required"/>
/// is set, accepted TCP connections are wrapped in <see cref="SslStream"/>
/// before being handed to <see cref="FixpSessionConnection"/>.</para>
/// </summary>
public sealed class FixpListenerHostedService : BackgroundService
{
    private readonly EntryPointListenerOptions _opts;
    private readonly IUserBotCredentialRegistry _credentials;
    private readonly IUserBotSessionRegistry _sessions;
    private readonly FixpOrderAdapter? _orders;
    private readonly IBotSessionConnectionDirectory? _connectionDirectory;
    private readonly BotOutboundCoordinator? _outboundCoordinator;
    private readonly RateLimiterRegistry? _rateLimiter;
    private readonly UserSessionCounter? _sessionCounter;
    private readonly Mtls.IClientCaTrustProvider? _caTrust;
    private readonly TimeProvider _clock;
    private readonly ILogger<FixpListenerHostedService> _logger;
    private readonly TaskCompletionSource<IPEndPoint> _boundTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TcpListener? _listener;
    private X509Certificate2? _tlsCert;
    private readonly AcceptConnectionRateLimiter _acceptLimiter;
    private readonly ConnectionGate _connectionGate;
    private readonly object _activeConnectionsGate = new();
    private readonly HashSet<Task> _activeConnections = new();

    public FixpListenerHostedService(
        IOptions<EntryPointListenerOptions> opts,
        IUserBotCredentialRegistry credentials,
        IUserBotSessionRegistry sessions,
        ILogger<FixpListenerHostedService> logger,
        IBotSessionConnectionDirectory? connectionDirectory = null,
        BotOutboundCoordinator? outboundCoordinator = null,
        RateLimiterRegistry? rateLimiter = null,
        UserSessionCounter? sessionCounter = null,
        TimeProvider? clock = null,
        Mtls.IClientCaTrustProvider? caTrust = null)
        : this(opts, credentials, sessions, logger, orders: null,
               connectionDirectory, outboundCoordinator, rateLimiter, sessionCounter, clock, caTrust)
    {
    }

    /// <summary>
    /// Internal constructor that also accepts a fully-wired
    /// <see cref="FixpOrderAdapter"/>. Invoked by the DI factory in
    /// <see cref="EntryPointListenerServiceCollectionExtensions.AddEntryPointListener"/>
    /// (issue #185) and by tests that exercise the order-path end to
    /// end. The adapter type is internal-only to the listener
    /// assembly, which is why this overload is internal.
    /// </summary>
    internal FixpListenerHostedService(
        IOptions<EntryPointListenerOptions> opts,
        IUserBotCredentialRegistry credentials,
        IUserBotSessionRegistry sessions,
        ILogger<FixpListenerHostedService> logger,
        FixpOrderAdapter? orders,
        IBotSessionConnectionDirectory? connectionDirectory = null,
        BotOutboundCoordinator? outboundCoordinator = null,
        RateLimiterRegistry? rateLimiter = null,
        UserSessionCounter? sessionCounter = null,
        TimeProvider? clock = null,
        Mtls.IClientCaTrustProvider? caTrust = null)
    {
        _opts = opts.Value;
        _credentials = credentials;
        _sessions = sessions;
        _logger = logger;
        _orders = orders;
        _connectionDirectory = connectionDirectory;
        _outboundCoordinator = outboundCoordinator;
        _rateLimiter = rateLimiter;
        _sessionCounter = sessionCounter;
        _caTrust = caTrust;
        _clock = clock ?? TimeProvider.System;
        _acceptLimiter = new AcceptConnectionRateLimiter(
            _opts.AcceptRateLimit.ConnectionsPerSecondPerIp,
            _opts.AcceptRateLimit.BurstPerIp);
        _connectionGate = new ConnectionGate(_opts.ConnectionCaps);
    }

    /// <summary>
    /// Resolves to the actual bound <see cref="IPEndPoint"/> (including
    /// OS-assigned port when configured with port 0) once the service has
    /// started listening.  Useful for integration tests.
    /// </summary>
    public Task<IPEndPoint> WhenBound => _boundTcs.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!EntryPointListenerOptionsValidator.TryParseEndpoint(_opts.Endpoint, out var endpoint))
        {
            _logger.LogError("Cannot start FIXP listener: invalid endpoint '{Ep}'.", _opts.Endpoint);
            _boundTcs.TrySetException(new InvalidOperationException(
                $"Invalid FIXP listener endpoint '{_opts.Endpoint}'."));
            return;
        }

        // Load TLS certificate once at startup
        if (!string.IsNullOrWhiteSpace(_opts.Tls.CertPath))
        {
            try
            {
                _tlsCert = LoadCertificate(_opts.Tls);
                _logger.LogInformation("FIXP listener: TLS certificate loaded from {CertPath}.", _opts.Tls.CertPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FIXP listener: failed to load TLS certificate from {CertPath}.", _opts.Tls.CertPath);
                _boundTcs.TrySetException(ex);
                return;
            }
        }

        if (_opts.Tls.Required && _tlsCert is null)
        {
            var msg = "FIXP listener: Tls:Required=true but no certificate could be loaded.";
            _logger.LogError(msg);
            _boundTcs.TrySetException(new InvalidOperationException(msg));
            return;
        }

        if (_opts.Tls.MtlsEnabled && _caTrust is null)
        {
            // Fail closed: mTLS is configured but no trust provider was wired,
            // so we cannot validate client certs. Better to refuse to start
            // than to silently admit (Optional) or reject every (Required)
            // connection.
            var msg = "FIXP listener: Tls:ClientCertificateMode is " +
                $"'{_opts.Tls.ClientCertificateMode}' but no client-CA trust provider is available.";
            _logger.LogError(msg);
            _boundTcs.TrySetException(new InvalidOperationException(msg));
            return;
        }

        FixpListenerMetrics.Enabled.Add(1);

        _listener = new TcpListener(endpoint);
        _listener.Start();

        var bound = (IPEndPoint)_listener.LocalEndpoint;
        _logger.LogInformation("FIXP listener bound on {Ep}.", bound);
        _boundTcs.TrySetResult(bound);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                    ApplyTcpOptions(client);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FIXP listener AcceptTcpClientAsync failed; retrying.");
                    continue;
                }

                if (!_acceptLimiter.Disabled && client.Client.RemoteEndPoint is IPEndPoint rep
                    && !_acceptLimiter.TryAccept(rep.Address, _clock))
                {
                    FixpListenerMetrics.ConnectionsRejected.Add(
                        1, new KeyValuePair<string, object?>("reason", "accept_rate_limit"));
                    _logger.LogWarning(
                        "fixp.accept.rate_limited remote={Remote} — closing before handshake.",
                        SafeRemote(client));
                    try { client.Dispose(); } catch { /* best effort */ }
                    continue;
                }

                var sourceIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (sourceIp is not null && _connectionGate.IsBlocked(sourceIp))
                {
                    FixpListenerMetrics.ConnectionsRejected.Add(
                        1, new KeyValuePair<string, object?>("reason", "ip_blocked"));
                    _logger.LogWarning("fixp.accept.ip_blocked remote={Remote}", SafeRemote(client));
                    try { client.Dispose(); } catch { /* best effort */ }
                    continue;
                }

                IDisposable? capLease = null;
                if (sourceIp is not null && !_connectionGate.TryAcquire(sourceIp, out capLease))
                {
                    FixpListenerMetrics.ConnectionsRejected.Add(
                        1, new KeyValuePair<string, object?>("reason", "max_connections"));
                    _logger.LogWarning("fixp.accept.max_connections remote={Remote}", SafeRemote(client));
                    try { client.Dispose(); } catch { /* best effort */ }
                    continue;
                }

                // Don't pass stoppingToken to Task.Run: on shutdown it could
                // skip the delegate entirely, leaking the cap lease + socket.
                // The handler always runs and releases the lease in finally;
                // the token still flows into the connection for cancellation.
                var connectionTask = Task.Run(
                    () => HandleAcceptedClientAsync(client, capLease, stoppingToken));
                TrackConnection(connectionTask);
            }
        }
        finally
        {
            _listener.Stop();
            await AwaitActiveConnectionsAsync().ConfigureAwait(false);
            _tlsCert?.Dispose();
        }
    }

    internal int ActiveConnectionTaskCount
    {
        get
        {
            lock (_activeConnectionsGate)
                return _activeConnections.Count;
        }
    }

    private void TrackConnection(Task connectionTask)
    {
        lock (_activeConnectionsGate)
            _activeConnections.Add(connectionTask);

        _ = connectionTask.ContinueWith(
            completed =>
            {
                lock (_activeConnectionsGate)
                    _activeConnections.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task AwaitActiveConnectionsAsync()
    {
        Task[] active;
        lock (_activeConnectionsGate)
            active = _activeConnections.ToArray();

        if (active.Length == 0)
            return;

        try
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "FIXP listener shutdown observed a faulted connection task.");
        }
    }

    private async Task HandleAcceptedClientAsync(TcpClient client, IDisposable? capLease, CancellationToken ct)
    {
        try
        {
            await HandleAcceptedClientCoreAsync(client, ct).ConfigureAwait(false);
        }
        finally
        {
            capLease?.Dispose();
        }
    }

    private async Task HandleAcceptedClientCoreAsync(TcpClient client, CancellationToken ct)
    {
        Stream stream;
        X509Certificate2? clientCert = null;
        try
        {
            if (_tlsCert is not null && _opts.Tls.Required)
            {
                var mtlsMode = _opts.Tls.ClientCertificateMode;
                var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                // Captured by the validation callback so the accept path can
                // emit the right metric/log and reject reason after the
                // handshake completes or throws (RFC §6).
                var mtlsOutcome = Mtls.ClientCertificateValidator.Outcome.Absent;
                var handshakeStart = Stopwatch.GetTimestamp();

                try
                {
                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshakeCts.CancelAfter(_opts.Tls.HandshakeTimeout);

                    var authOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _tlsCert,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        // Request a client cert for both Optional and Required;
                        // the mode decides acceptance inside the callback.
                        ClientCertificateRequired = mtlsMode != ClientCertificateMode.None,
                    };

                    if (mtlsMode != ClientCertificateMode.None)
                    {
                        var snapshot = _caTrust!.Current;
                        var requireEku = _opts.Tls.RequireClientAuthEku;
                        authOptions.RemoteCertificateValidationCallback =
                            (_, cert, chain, _) =>
                            {
                                var leaf = AsX509Certificate2(cert);
                                mtlsOutcome = Mtls.ClientCertificateValidator.Validate(
                                    leaf, mtlsMode, snapshot, requireEku, chain as X509Chain);
                                if (mtlsOutcome.IsAdmitted())
                                {
                                    clientCert = leaf;
                                    return true;
                                }

                                if (!ReferenceEquals(leaf, cert)) leaf?.Dispose();
                                return false;
                            };
                    }

                    await sslStream.AuthenticateAsServerAsync(authOptions, handshakeCts.Token)
                        .ConfigureAwait(false);

                    var handshakeOutcome = mtlsMode != ClientCertificateMode.None ? "mtls" : "ok";
                    FixpListenerMetrics.TlsHandshakeDurationMs.Record(
                        Stopwatch.GetElapsedTime(handshakeStart).TotalMilliseconds,
                        new KeyValuePair<string, object?>("outcome", "ok"));
                    FixpListenerMetrics.TlsHandshakeCompleted.Add(1);
                    _logger.LogInformation("fixp.tls.handshake.completed remote={Remote}", SafeRemote(client));

                    if (mtlsMode != ClientCertificateMode.None)
                    {
                        FixpListenerMetrics.MtlsClientCertsTotal.Add(
                            1, new KeyValuePair<string, object?>("outcome", mtlsOutcome.ToTag()));
                        _logger.LogInformation(
                            "fixp.mtls.handshake.completed remote={Remote} outcome={Outcome} thumbprint={Thumbprint}",
                            SafeRemote(client), mtlsOutcome.ToTag(), SafeThumbprint(clientCert));
                    }
                }
                catch (Exception ex)
                {
                    var failOutcome = mtlsOutcome.IsAdmitted() ? "tls" : "mtls";
                    FixpListenerMetrics.TlsHandshakeDurationMs.Record(
                        Stopwatch.GetElapsedTime(handshakeStart).TotalMilliseconds,
                        new KeyValuePair<string, object?>("outcome", failOutcome));
                    // Distinguish an mTLS policy rejection (cert absent /
                    // untrusted / denied / bad EKU) from a generic TLS failure
                    // so the reason tag and log are actionable.
                    if (!mtlsOutcome.IsAdmitted())
                    {
                        FixpListenerMetrics.ConnectionsRejected.Add(
                            1, new KeyValuePair<string, object?>("reason", "mtls"));
                        FixpListenerMetrics.MtlsClientCertsTotal.Add(
                            1, new KeyValuePair<string, object?>("outcome", mtlsOutcome.ToTag()));
                        _logger.LogWarning(
                            "fixp.mtls.handshake.rejected remote={Remote} outcome={Outcome}",
                            SafeRemote(client), mtlsOutcome.ToTag());
                    }
                    else
                    {
                        FixpListenerMetrics.ConnectionsRejected.Add(
                            1, new KeyValuePair<string, object?>("reason", "tls"));
                        _logger.LogWarning(ex, "fixp.tls.handshake.failed remote={Remote}", SafeRemote(client));
                    }

                    clientCert?.Dispose();
                    sslStream.Dispose();
                    client.Dispose();
                    return;
                }
                stream = sslStream;
            }
            else
            {
                stream = client.GetStream();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fixp.accept.error remote={Remote}", SafeRemote(client));
            client.Dispose();
            return;
        }

        _logger.LogDebug("fixp.accept remote={Remote}", SafeRemote(client));

        var conn = new FixpSessionConnection(
            client, stream, _credentials, _sessions, _logger,
            _orders, _connectionDirectory, _outboundCoordinator, _opts, _clock,
            _rateLimiter, _sessionCounter, clientCert);
        await conn.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Coerces the BCL-supplied <see cref="X509Certificate"/> to the
    /// <see cref="X509Certificate2"/> our validation and binding paths need.
    /// Modern .NET already passes an <see cref="X509Certificate2"/>; the
    /// fallback copy is defensive.
    /// </summary>
    private static X509Certificate2? AsX509Certificate2(X509Certificate? cert) => cert switch
    {
        null => null,
        X509Certificate2 c2 => c2,
        _ => new X509Certificate2(cert),
    };

    private static string SafeThumbprint(X509Certificate2? cert) =>
        cert is null
            ? "-"
            : cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);

    /// <summary>
    /// Test-only hook fired immediately after
    /// <see cref="TryApplyTcpOptions"/> runs against an accepted client.
    /// Lets integration tests observe the accept-path actually applies
    /// the configured <see cref="FixpTcpOptions"/> (issue #205).
    /// </summary>
    internal static event Action<TcpClient>? AcceptedClientConfigured;

    private void ApplyTcpOptions(TcpClient client)
    {
        // RFC §5.9 / P11 — disable Nagle and pin SO_SNDBUF / SO_RCVBUF on
        // every accepted FIXP connection. Failures are logged but
        // non-fatal: the connection is still functional with OS defaults.
        if (!TryApplyTcpOptions(client, _opts.Tcp, out var ex))
        {
            _logger.LogWarning(ex,
                "fixp.accept.tcp_options.failed remote={Remote}", SafeRemote(client));
        }

        AcceptedClientConfigured?.Invoke(client);
    }

    /// <summary>
    /// Applies <paramref name="tcp"/> to <paramref name="client"/>
    /// (NoDelay + send/receive buffer sizing). Returns <c>false</c> and
    /// surfaces the captured exception when the kernel rejects any of
    /// the settings — callers may ignore and proceed with OS defaults.
    /// Exposed <c>internal</c> for direct test coverage of the
    /// FIXP/OUCH socket-config contract (issue #205).
    /// </summary>
    internal static bool TryApplyTcpOptions(TcpClient client, FixpTcpOptions tcp, out Exception? error)
    {
        try
        {
            client.NoDelay = tcp.NoDelay;
            client.SendBufferSize = tcp.SendBufferBytes;
            client.ReceiveBufferSize = tcp.ReceiveBufferBytes;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static X509Certificate2 LoadCertificate(EntryPointListenerOptions.TlsOptions tls)
    {
        if (tls.IsPfx)
        {
            return X509CertificateLoader.LoadPkcs12FromFile(tls.CertPath!, tls.Password);
        }

        // PEM: load from cert + key files, then re-export to PFX-backed
        // cert for SslStream compatibility.
        var pem = X509Certificate2.CreateFromPemFile(tls.CertPath!, tls.KeyPath);
        var exported = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null);
        pem.Dispose();
        return exported;
    }

    private static string SafeRemote(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint switch
            {
                IPEndPoint ip => $"{ip.Address}:{ip.Port}",
                { } ep => ep.ToString() ?? "?",
                _ => "?",
            };
        }
        catch { return "?"; }
    }
}
