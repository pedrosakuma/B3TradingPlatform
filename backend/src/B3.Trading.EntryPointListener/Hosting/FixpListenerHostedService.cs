using System.Net;
using System.Net.Sockets;
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
/// <para>TLS in-socket is deferred to sub-issue E.  When
/// <see cref="EntryPointListenerOptions.TlsOptions.Required"/> is set the
/// boot guard has already enforced the configuration, but connections are
/// still served over plaintext; a loud warning is logged at startup.</para>
/// </summary>
public sealed class FixpListenerHostedService : BackgroundService
{
    private readonly EntryPointListenerOptions _opts;
    private readonly IUserBotCredentialRegistry _credentials;
    private readonly IUserBotSessionRegistry _sessions;
    private readonly FixpOrderAdapter? _orders;
    private readonly IBotSessionConnectionDirectory? _connectionDirectory;
    private readonly BotOutboundCoordinator? _outboundCoordinator;
    private readonly TimeProvider _clock;
    private readonly ILogger<FixpListenerHostedService> _logger;
    private readonly TaskCompletionSource<IPEndPoint> _boundTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TcpListener? _listener;

    public FixpListenerHostedService(
        IOptions<EntryPointListenerOptions> opts,
        IUserBotCredentialRegistry credentials,
        IUserBotSessionRegistry sessions,
        ILogger<FixpListenerHostedService> logger,
        SymbolDirectory? symbols = null,
        OrderSubmissionService? submit = null,
        OrderCancelService? cancel = null,
        IUserBotOrderMappingRegistry? botMappings = null,
        IBotSessionConnectionDirectory? connectionDirectory = null,
        BotOutboundCoordinator? outboundCoordinator = null,
        TimeProvider? clock = null)
    {
        _opts = opts.Value;
        _credentials = credentials;
        _sessions = sessions;
        _logger = logger;
        _connectionDirectory = connectionDirectory;
        _outboundCoordinator = outboundCoordinator;
        _clock = clock ?? TimeProvider.System;
        // Sub-issue #171 (E): the order/cancel adapter is only wired when
        // the host has registered the full submit pipeline. Tests (and
        // any future handshake-only mode) leave the deps null and the
        // listener falls back to the v0 "ignore application messages"
        // behaviour.
        if (symbols is not null && submit is not null && cancel is not null && botMappings is not null)
        {
            _orders = new FixpOrderAdapter(symbols, submit, cancel, botMappings, logger);
        }
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

        if (_opts.Tls.Required)
        {
            _logger.LogWarning(
                "FIXP listener: Tls:Required=true but in-socket TLS is not yet implemented " +
                "(sub-issue E). Serving PLAINTEXT on {Ep}. Do NOT expose to the public internet.",
                endpoint);
        }

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
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FIXP listener AcceptTcpClientAsync failed; retrying.");
                    continue;
                }

                var conn = new FixpSessionConnection(
                    client, _credentials, _sessions, _logger,
                    _orders, _connectionDirectory, _outboundCoordinator, _opts, _clock);
                _ = Task.Run(() => conn.RunAsync(stoppingToken), stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
        }
    }
}
