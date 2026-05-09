using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private readonly ILogger<FixpListenerHostedService> _logger;
    private readonly TaskCompletionSource<IPEndPoint> _boundTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TcpListener? _listener;

    public FixpListenerHostedService(
        IOptions<EntryPointListenerOptions> opts,
        ILogger<FixpListenerHostedService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
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

                var sessionId = AllocateSessionId();
                _logger.LogDebug("FIXP connection accepted; internalSessionId={Id}.", sessionId);

                var conn = new FixpSessionConnection(client, _logger);
                _ = Task.Run(() => conn.RunAsync(stoppingToken), stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    /// <summary>
    /// Generates a random non-zero uint32 used as an internal connection
    /// identifier for log correlation.  Sub-issue D will replace this with
    /// a monotonic sequence-version manager.
    /// </summary>
    private static uint AllocateSessionId()
    {
        Span<byte> buf = stackalloc byte[4];
        uint id;
        do
        {
            RandomNumberGenerator.Fill(buf);
            id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf);
        }
        while (id == 0);
        return id;
    }
}
