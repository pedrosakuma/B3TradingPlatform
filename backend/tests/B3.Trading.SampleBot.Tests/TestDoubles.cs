using System.Net;
using System.Net.Http;
using System.Text;
using B3.Trading.SampleBot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.SampleBot.Tests;

internal sealed class StubAuthProvider : ISampleBotAuthProvider
{
    private readonly Func<CancellationToken, Task<AuthenticatedSession>> _authenticate;

    public StubAuthProvider(AuthenticatedSession session)
        : this(_ => Task.FromResult(session))
    {
    }

    public StubAuthProvider(Func<CancellationToken, Task<AuthenticatedSession>> authenticate)
    {
        _authenticate = authenticate;
    }

    public Task<AuthenticatedSession> AuthenticateAsync(CancellationToken cancellationToken) => _authenticate(cancellationToken);
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _handler(request, cancellationToken);

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}

internal sealed class RecordingObserver : IPrivateFeedObserver
{
    private readonly Action? _onSecondConnect;

    public RecordingObserver(Action? onSecondConnect = null)
    {
        _onSecondConnect = onSecondConnect;
    }

    public List<bool> ConnectEvents { get; } = new();

    public List<PrivateFeedFrame> Frames { get; } = new();

    public int DisconnectCount { get; private set; }

    public Task OnConnectedAsync(bool isReconnect, CancellationToken cancellationToken)
    {
        ConnectEvents.Add(isReconnect);
        if (ConnectEvents.Count == 2)
            _onSecondConnect?.Invoke();
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(Exception? error, CancellationToken cancellationToken)
    {
        DisconnectCount++;
        return Task.CompletedTask;
    }

    public Task OnFrameAsync(PrivateFeedFrame frame, CancellationToken cancellationToken)
    {
        Frames.Add(frame);
        return Task.CompletedTask;
    }
}

internal sealed class FakeWebSocketConnection : ISampleBotWebSocketConnection
{
    private readonly Queue<string?> _messages;

    public FakeWebSocketConnection(IEnumerable<string?> messages)
    {
        _messages = new Queue<string?>(messages);
    }

    public List<string> SentPayloads { get; } = new();

    public Task SendTextAsync(string payload, CancellationToken cancellationToken)
    {
        SentPayloads.Add(payload);
        return Task.CompletedTask;
    }

    public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        if (_messages.Count == 0)
            return Task.FromResult<string?>(null);
        return Task.FromResult(_messages.Dequeue());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeWebSocketConnectionFactory : ISampleBotWebSocketConnectionFactory
{
    private readonly Queue<ISampleBotWebSocketConnection> _connections;

    public FakeWebSocketConnectionFactory(IEnumerable<ISampleBotWebSocketConnection> connections)
    {
        _connections = new Queue<ISampleBotWebSocketConnection>(connections);
    }

    public List<(Uri Uri, string Token)> ConnectCalls { get; } = new();

    public Task<ISampleBotWebSocketConnection> ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken)
    {
        ConnectCalls.Add((uri, bearerToken));
        if (_connections.Count == 0)
            throw new InvalidOperationException("No fake websocket connection available.");
        return Task.FromResult(_connections.Dequeue());
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}
