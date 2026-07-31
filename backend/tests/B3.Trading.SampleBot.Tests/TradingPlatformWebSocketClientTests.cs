using B3.Trading.SampleBot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.SampleBot.Tests;

public sealed class TradingPlatformWebSocketClientTests
{
    [Fact]
    public void PrivateFeedProtocol_ParsesTypedOrderSnapshot()
    {
        var frame = PrivateFeedProtocol.Parse(
            """
            {"type":"snapshot","channel":"orders.me","seq":0,"data":[{"clOrdId":"101","symbol":"PETR4","securityId":4321,"side":"Buy","type":"Limit","quantity":100,"leavesQuantity":100,"cumulativeQuantity":0,"price":30.0,"status":"Working"}]}
            """);

        var snapshot = Assert.IsType<OrdersSnapshotFrame>(frame);
        Assert.Single(snapshot.Orders);
        Assert.Equal("101", snapshot.Orders[0].ClOrdId);
    }

    [Fact]
    public void PrivateFeedProtocol_ParsesPhaseSnapshot()
    {
        var frame = PrivateFeedProtocol.Parse(
            """
            {"type":"snapshot","channel":"phases.PETR4","seq":4,"data":{"phase":"Open","at":"2026-07-29T00:00:00Z"}}
            """);

        var snapshot = Assert.IsType<PhaseSnapshotFrame>(frame);
        Assert.Equal("PETR4", snapshot.Symbol);
        Assert.Equal("Open", snapshot.Phase.Phase);
    }

    [Fact]
    public void BuildAuthenticatedUri_AppendsRealEscapedToken()
    {
        var uri = ClientWebSocketConnectionFactory.BuildAuthenticatedUri(
            new Uri("wss://trading.local/ws?existing=1"),
            "abc def/ghi==");

        Assert.Equal("existing=1&access_token=abc%20def%2Fghi%3D%3D", uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped));
    }

    [Fact]
    public async Task RunAsync_ReconnectsAndResubscribesAfterDisconnect()
    {
        using var cts = new CancellationTokenSource();
        var firstConnection = new FakeWebSocketConnection([
            """{"type":"snapshot","channel":"orders.me","seq":0,"data":[]}""",
            """{"type":"snapshot","channel":"executions.me","seq":0,"data":[]}""",
            """{"type":"snapshot","channel":"positions.me","seq":0,"data":[]}""",
            null,
        ]);
        var secondConnection = new FakeWebSocketConnection([
            """{"type":"snapshot","channel":"orders.me","seq":0,"data":[]}""",
            null,
        ]);
        var factory = new FakeWebSocketConnectionFactory([firstConnection, secondConnection]);
        var sessionCache = new AuthenticatedSessionCache(
            new StubAuthProvider(new AuthenticatedSession("internal-jwt", DateTimeOffset.UtcNow.AddMinutes(5), SampleBotAuthMode.InternalToken)),
            TimeProvider.System);
        var client = new TradingPlatformWebSocketClient(
            sessionCache,
            factory,
            Options.Create(new SampleBotOptions
            {
                BaseUrl = "https://trading.local",
                ReconnectDelay = TimeSpan.Zero,
            }),
            NullLogger<TradingPlatformWebSocketClient>.Instance);
        var observer = new RecordingObserver(() => cts.Cancel());

        await client.RunAsync(observer, PrivateFeedProtocol.PrivateChannels, cts.Token);

        Assert.Equal([false, true], observer.ConnectEvents);
        Assert.Equal(1, observer.DisconnectCount);
        Assert.Equal(2, factory.ConnectCalls.Count);
        Assert.Single(firstConnection.SentPayloads);
        Assert.Single(secondConnection.SentPayloads);
        Assert.Equal(PrivateFeedProtocol.BuildSubscribeCommand(PrivateFeedProtocol.PrivateChannels), firstConnection.SentPayloads[0]);
        Assert.Equal(PrivateFeedProtocol.BuildSubscribeCommand(PrivateFeedProtocol.PrivateChannels), secondConnection.SentPayloads[0]);
    }

    [Fact]
    public void BuildWebSocketUri_ConvertsHttpsBaseAddress()
    {
        var uri = TradingPlatformWebSocketClient.BuildWebSocketUri("https://trading.local/api/");

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("/api/ws", uri.AbsolutePath);
    }
}
