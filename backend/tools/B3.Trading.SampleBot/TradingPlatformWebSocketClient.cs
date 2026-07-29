using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace B3.Trading.SampleBot;

internal interface IPrivateFeedObserver
{
    Task OnConnectedAsync(bool isReconnect, CancellationToken cancellationToken);

    Task OnDisconnectedAsync(Exception? error, CancellationToken cancellationToken);

    Task OnFrameAsync(PrivateFeedFrame frame, CancellationToken cancellationToken);
}

internal interface ISampleBotWebSocketConnection : IAsyncDisposable
{
    Task SendTextAsync(string payload, CancellationToken cancellationToken);

    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}

internal interface ISampleBotWebSocketConnectionFactory
{
    Task<ISampleBotWebSocketConnection> ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketConnectionFactory : ISampleBotWebSocketConnectionFactory
{
    internal static readonly string AccessTokenQueryParameter = "access_token";

    public async Task<ISampleBotWebSocketConnection> ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildAuthenticatedUri(uri, bearerToken), cancellationToken);
        return new ClientWebSocketConnection(socket);
    }

    internal static Uri BuildAuthenticatedUri(Uri uri, string bearerToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var builder = new UriBuilder(uri);
        var tokenParam = $"{AccessTokenQueryParameter}={Uri.EscapeDataString(bearerToken)}";
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? tokenParam
            : $"{builder.Query.TrimStart('?')}&{tokenParam}";
        return builder.Uri;
    }
}

internal sealed class TradingPlatformWebSocketClient
{
    private readonly AuthenticatedSessionCache _sessionCache;
    private readonly ISampleBotWebSocketConnectionFactory _connectionFactory;
    private readonly SampleBotOptions _options;
    private readonly ILogger<TradingPlatformWebSocketClient> _logger;

    public TradingPlatformWebSocketClient(
        AuthenticatedSessionCache sessionCache,
        ISampleBotWebSocketConnectionFactory connectionFactory,
        Microsoft.Extensions.Options.IOptions<SampleBotOptions> options,
        ILogger<TradingPlatformWebSocketClient> logger)
    {
        _sessionCache = sessionCache;
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(
        IPrivateFeedObserver observer,
        IReadOnlyList<string> channels,
        CancellationToken cancellationToken)
    {
        var isReconnect = false;
        var websocketUri = BuildWebSocketUri(_options.BaseUrl);

        while (!cancellationToken.IsCancellationRequested)
        {
            Exception? disconnectError = null;
            try
            {
                var session = await _sessionCache.GetAsync(cancellationToken);
                await using var socket = await _connectionFactory.ConnectAsync(websocketUri, session.Token, cancellationToken);
                await observer.OnConnectedAsync(isReconnect, cancellationToken);
                await socket.SendTextAsync(PrivateFeedProtocol.BuildSubscribeCommand(channels), cancellationToken);
                _logger.LogInformation("Connected to {WebSocketUri} and subscribed to private channels.", websocketUri);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var payload = await socket.ReceiveTextAsync(cancellationToken);
                    if (payload is null)
                        break;

                    var frame = PrivateFeedProtocol.Parse(payload);
                    await observer.OnFrameAsync(frame, cancellationToken);
                    if (frame is ProtocolErrorFrame { Code: "slow_consumer_resync_required" })
                    {
                        disconnectError = new InvalidOperationException("Server requested websocket resync.");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                disconnectError = ex;
                _logger.LogWarning(ex, "Private websocket connection ended; retrying.");
                if (ex is WebSocketException)
                    _sessionCache.Invalidate();
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            isReconnect = true;
            await observer.OnDisconnectedAsync(disconnectError, cancellationToken);
            await Task.Delay(_options.ReconnectDelay, cancellationToken);
        }
    }

    internal static Uri BuildWebSocketUri(string baseUrl)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        var httpUri = new Uri(baseUri, "ws");
        var builder = new UriBuilder(httpUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? Uri.UriSchemeWss : Uri.UriSchemeWs,
            Port = httpUri.Port,
        };
        return builder.Uri;
    }
}

internal sealed class ClientWebSocketConnection : ISampleBotWebSocketConnection
{
    private readonly ClientWebSocket _socket;

    public ClientWebSocketConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public async Task SendTextAsync(string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var builder = new StringBuilder();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
                return builder.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "samplebot shutdown", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }

        _socket.Dispose();
    }
}
