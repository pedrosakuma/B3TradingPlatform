using System.Net.Sockets;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Tests.Integration;

/// <summary>
/// #529 public-hardening: source-IP deny-list and concurrent-connection caps
/// drop a peer in the accept loop before any FIXP bytes flow. Loopback is
/// the deny target so the connection is reset immediately after connect.
/// </summary>
public sealed class ConnectionCapsIntegrationTests
{
    private static IHost BuildHost(Dictionary<string, string?> extra)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["Trading:EntryPointListener:Enabled"] = "true",
            ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
        };
        foreach (var kv in extra) cfg[kv.Key] = kv.Value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(cfg).Build();
        return new HostBuilder()
            .ConfigureServices((_, s) =>
            {
                s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                s.AddSingleton<InMemoryUserBotCredentialRegistry>();
                s.AddSingleton<IUserBotCredentialRegistry>(sp => sp.GetRequiredService<InMemoryUserBotCredentialRegistry>());
                s.AddSingleton<InMemoryUserBotSessionRegistry>();
                s.AddSingleton<IUserBotSessionRegistry>(sp => sp.GetRequiredService<InMemoryUserBotSessionRegistry>());
                s.AddNoopOrderPathStubs();
                s.AddEntryPointListener(config);
            })
            .Build();
    }

    [Fact]
    public async Task DeniedIp_ConnectionDroppedImmediately()
    {
        using var host = BuildHost(new()
        {
            ["Trading:EntryPointListener:ConnectionCaps:DeniedIps:0"] = "127.0.0.1",
        });
        var listener = host.Services.GetRequiredService<FixpListenerHostedService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(cts.Token);
        var ep = await listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var buf = new byte[1];
        var n = await tcp.GetStream().ReadAsync(buf, cts.Token); // server closes => 0
        Assert.Equal(0, n);

        await host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task AllowList_ExcludesLoopback_Drops()
    {
        using var host = BuildHost(new()
        {
            ["Trading:EntryPointListener:ConnectionCaps:AllowedIps:0"] = "10.0.0.1",
        });
        var listener = host.Services.GetRequiredService<FixpListenerHostedService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(cts.Token);
        var ep = await listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        var n = await tcp.GetStream().ReadAsync(new byte[1], cts.Token);
        Assert.Equal(0, n);

        await host.StopAsync(cts.Token);
    }
}
