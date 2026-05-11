using System.Net;
using System.Net.Sockets;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

/// <summary>
/// Issue #205 / RFC §5.9 (P11). Verifies that
/// <see cref="FixpListenerHostedService"/> applies the configured
/// <c>FixpTcpOptions</c> (NoDelay + send/receive buffer sizing) to every
/// accepted client socket immediately after <c>AcceptTcpClientAsync</c>.
/// </summary>
public class TcpSocketOptionsTests
{
    private const int CustomSend = 96 * 1024;
    private const int CustomRecv = 128 * 1024;

    [Fact]
    public void DefaultOptions_MatchRfcDefaults()
    {
        var opts = new FixpTcpOptions();

        Assert.True(opts.NoDelay);
        Assert.Equal(64 * 1024, opts.SendBufferBytes);
        Assert.Equal(64 * 1024, opts.ReceiveBufferBytes);
    }

    [Fact]
    public async Task TryApplyTcpOptions_SetsNoDelayAndBufferSizesOnAcceptedSocket()
    {
        // White-box exercise of the exact code path
        // FixpListenerHostedService runs immediately after
        // AcceptTcpClientAsync. We open a real loopback listener,
        // accept one client, and assert the kernel echoes back the
        // configured socket properties.
        var srvListener = new TcpListener(IPAddress.Loopback, 0);
        srvListener.Start();
        try
        {
            var port = ((IPEndPoint)srvListener.LocalEndpoint).Port;
            var acceptTask = srvListener.AcceptTcpClientAsync();

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port)
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

            var tcp = new FixpTcpOptions
            {
                NoDelay = true,
                SendBufferBytes = CustomSend,
                ReceiveBufferBytes = CustomRecv,
            };

            var ok = FixpListenerHostedService.TryApplyTcpOptions(server, tcp, out var err);

            Assert.True(ok);
            Assert.Null(err);
            Assert.True(server.NoDelay);
            // Kernels are free to round buffer sizes upward; assert at
            // least the configured value made it through.
            Assert.True(server.SendBufferSize >= CustomSend,
                $"SendBufferSize was {server.SendBufferSize}, expected >= {CustomSend}");
            Assert.True(server.ReceiveBufferSize >= CustomRecv,
                $"ReceiveBufferSize was {server.ReceiveBufferSize}, expected >= {CustomRecv}");
        }
        finally
        {
            srvListener.Stop();
        }
    }

    [Fact]
    public void TryApplyTcpOptions_OnDisposedSocket_FailsGracefully()
    {
        var disposed = new TcpClient();
        disposed.Dispose();

        var ok = FixpListenerHostedService.TryApplyTcpOptions(
            disposed, new FixpTcpOptions(), out var err);

        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task ListenerAcceptPath_AppliesTcpOptionsToServerSocket()
    {
        // Regression guard: asserts the FixpListenerHostedService accept
        // loop itself (not just the helper) applies FixpTcpOptions to
        // every accepted client. If the apply call were ever removed
        // from the accept path, this test would fail.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:EntryPointListener:Enabled"] = "true",
                ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
                ["Trading:EntryPointListener:Tcp:NoDelay"] = "true",
                ["Trading:EntryPointListener:Tcp:SendBufferBytes"] = CustomSend.ToString(),
                ["Trading:EntryPointListener:Tcp:ReceiveBufferBytes"] = CustomRecv.ToString(),
            })
            .Build();

        using var host = new HostBuilder()
            .ConfigureServices((_, s) =>
            {
                s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                s.AddSingleton<InMemoryUserBotCredentialRegistry>();
                s.AddSingleton<IUserBotCredentialRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotCredentialRegistry>());
                s.AddSingleton<InMemoryUserBotSessionRegistry>();
                s.AddSingleton<IUserBotSessionRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotSessionRegistry>());
                s.AddNoopOrderPathStubs();
                s.AddEntryPointListener(config);
            })
            .Build();

        var captured = new TaskCompletionSource<(bool noDelay, int snd, int rcv)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Action<TcpClient> handler = c =>
        {
            try
            {
                captured.TrySetResult((c.NoDelay, c.SendBufferSize, c.ReceiveBufferSize));
            }
            catch (Exception ex)
            {
                captured.TrySetException(ex);
            }
        };
        FixpListenerHostedService.AcceptedClientConfigured += handler;

        try
        {
            await host.StartAsync();
            var listener = host.Services.GetRequiredService<FixpListenerHostedService>();
            var bound = await listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5));

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, bound.Port)
                .WaitAsync(TimeSpan.FromSeconds(5));

            var snapshot = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(snapshot.noDelay);
            Assert.True(snapshot.snd >= CustomSend,
                $"server-side SendBufferSize was {snapshot.snd}, expected >= {CustomSend}");
            Assert.True(snapshot.rcv >= CustomRecv,
                $"server-side ReceiveBufferSize was {snapshot.rcv}, expected >= {CustomRecv}");
        }
        finally
        {
            FixpListenerHostedService.AcceptedClientConfigured -= handler;
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ListenerHost_BindsTcpOptionsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:EntryPointListener:Enabled"] = "true",
                ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
                ["Trading:EntryPointListener:Tcp:NoDelay"] = "true",
                ["Trading:EntryPointListener:Tcp:SendBufferBytes"] = CustomSend.ToString(),
                ["Trading:EntryPointListener:Tcp:ReceiveBufferBytes"] = CustomRecv.ToString(),
            })
            .Build();

        using var host = new HostBuilder()
            .ConfigureServices((_, s) =>
            {
                s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                s.AddSingleton<InMemoryUserBotCredentialRegistry>();
                s.AddSingleton<IUserBotCredentialRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotCredentialRegistry>());
                s.AddSingleton<InMemoryUserBotSessionRegistry>();
                s.AddSingleton<IUserBotSessionRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotSessionRegistry>());
                s.AddNoopOrderPathStubs();
                s.AddEntryPointListener(config);
            })
            .Build();

        await host.StartAsync();
        try
        {
            var opts = host.Services
                .GetRequiredService<IOptions<EntryPointListenerOptions>>().Value;

            Assert.True(opts.Tcp.NoDelay);
            Assert.Equal(CustomSend, opts.Tcp.SendBufferBytes);
            Assert.Equal(CustomRecv, opts.Tcp.ReceiveBufferBytes);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }
}
