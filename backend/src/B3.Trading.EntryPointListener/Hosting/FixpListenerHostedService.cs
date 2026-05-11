using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
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
    private readonly TimeProvider _clock;
    private readonly ILogger<FixpListenerHostedService> _logger;
    private readonly TaskCompletionSource<IPEndPoint> _boundTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TcpListener? _listener;
    private X509Certificate2? _tlsCert;

    public FixpListenerHostedService(
        IOptions<EntryPointListenerOptions> opts,
        IUserBotCredentialRegistry credentials,
        IUserBotSessionRegistry sessions,
        ILogger<FixpListenerHostedService> logger,
        IBotSessionConnectionDirectory? connectionDirectory = null,
        BotOutboundCoordinator? outboundCoordinator = null,
        RateLimiterRegistry? rateLimiter = null,
        UserSessionCounter? sessionCounter = null,
        TimeProvider? clock = null)
        : this(opts, credentials, sessions, logger, orders: null,
               connectionDirectory, outboundCoordinator, rateLimiter, sessionCounter, clock)
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
        TimeProvider? clock = null)
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
        _clock = clock ?? TimeProvider.System;
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

                _ = Task.Run(() => HandleAcceptedClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
            _tlsCert?.Dispose();
        }
    }

    private async Task HandleAcceptedClientAsync(TcpClient client, CancellationToken ct)
    {
        Stream stream;
        try
        {
            if (_tlsCert is not null && _opts.Tls.Required)
            {
                var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _tlsCert,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false,
                    }, handshakeCts.Token).ConfigureAwait(false);
                    FixpListenerMetrics.TlsHandshakeCompleted.Add(1);
                    _logger.LogInformation("fixp.tls.handshake.completed remote={Remote}", SafeRemote(client));
                }
                catch (Exception ex)
                {
                    FixpListenerMetrics.ConnectionsRejected.Add(1, new KeyValuePair<string, object?>("reason", "tls"));
                    _logger.LogWarning(ex, "fixp.tls.handshake.failed remote={Remote}", SafeRemote(client));
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
            _rateLimiter, _sessionCounter);
        await conn.RunAsync(ct).ConfigureAwait(false);
    }

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
