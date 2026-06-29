using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

/// <summary>
/// Many-bot capacity + hostile-connection coverage (#534). A real listener
/// is driven by N concurrent bot sessions through the full Negotiate→
/// Establish→Terminate handshake; the listener must service every one and
/// stay up. A separate hostile path floods garbage bytes and proves the
/// listener closes per-connection without crashing the accept loop.
/// </summary>
public class ManyBotLoadTests
{
    private const ushort SchemaIdV6 = 1;

    private sealed record HostBundle(
        IHost Host, FixpListenerHostedService Listener,
        InMemoryUserBotCredentialRegistry Credentials, InMemoryUserBotSessionRegistry Sessions);

    private static HostBundle BuildHost()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:EntryPointListener:Enabled"] = "true",
                ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
            })
            .Build();

        var host = new HostBuilder()
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

        return new HostBundle(host,
            host.Services.GetRequiredService<FixpListenerHostedService>(),
            host.Services.GetRequiredService<InMemoryUserBotCredentialRegistry>(),
            host.Services.GetRequiredService<InMemoryUserBotSessionRegistry>());
    }

    private static byte[] BuildNegotiateFrame(uint sessionId, ulong sessionVerId, string token)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var bodyLen = NegotiateData.BLOCK_LENGTH + 1 + tokenBytes.Length;
        var body = new byte[bodyLen];
        ref var msg = ref MemoryMarshal.AsRef<NegotiateData>(body.AsSpan(0, NegotiateData.BLOCK_LENGTH));
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        body[NegotiateData.BLOCK_LENGTH] = (byte)tokenBytes.Length;
        tokenBytes.CopyTo(body, NegotiateData.BLOCK_LENGTH + 1);
        var buf = new byte[SofhFrameWriter.FrameSize(body.Length)];
        SofhFrameWriter.WriteFrame(buf, (ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID, SchemaIdV6, 6, body);
        return buf;
    }

    private static byte[] BuildEstablishFrame(uint sessionId, ulong sessionVerId)
    {
        var body = new byte[EstablishData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<EstablishData>(body.AsSpan());
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        var buf = new byte[SofhFrameWriter.FrameSize(body.Length)];
        SofhFrameWriter.WriteFrame(buf, (ushort)EstablishData.BLOCK_LENGTH, (ushort)EstablishData.MESSAGE_ID, SchemaIdV6, 6, body);
        return buf;
    }

    private static byte[] BuildTerminateFrame(uint sessionId, ulong sessionVerId)
    {
        var body = new byte[TerminateData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<TerminateData>(body.AsSpan());
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        msg.TerminationCode = TerminationCode.FINISHED;
        var buf = new byte[SofhFrameWriter.FrameSize(body.Length)];
        SofhFrameWriter.WriteFrame(buf, (ushort)TerminateData.BLOCK_LENGTH, (ushort)TerminateData.MESSAGE_ID, SchemaIdV6, 0, body);
        return buf;
    }

    private static async Task<ushort> ReadTemplateAsync(SofhFrameReader reader, NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        while (true)
        {
            if (reader.TryReadFrame(out var frame)) return frame.TemplateId;
            if (reader.HasProtocolError) return 0;
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return 0;
            reader.Append(buf.AsSpan(0, n));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ManyConcurrentBots_AllHandshakeSucceed()
    {
        const int bots = 25;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var tasks = Enumerable.Range(0, bots).Select(async i =>
            {
                var created = await bundle.Credentials.CreateAsync($"user-{i}", $"bot-{i}", cts.Token);
                var st = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token);
                var stream = tcp.GetStream();
                var reader = new SofhFrameReader();
                await stream.WriteAsync(BuildNegotiateFrame(st.SessionId, st.CurrentVer, created.PlainToken), cts.Token);
                var neg = await ReadTemplateAsync(reader, stream, cts.Token);
                await stream.WriteAsync(BuildEstablishFrame(st.SessionId, st.CurrentVer), cts.Token);
                var est = await ReadTemplateAsync(reader, stream, cts.Token);
                await stream.WriteAsync(BuildTerminateFrame(st.SessionId, st.CurrentVer), cts.Token);
                var term = await ReadTemplateAsync(reader, stream, cts.Token);
                return neg == (ushort)NegotiateResponseData.MESSAGE_ID
                    && est == (ushort)EstablishAckData.MESSAGE_ID
                    && term == (ushort)TerminateData.MESSAGE_ID;
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            Assert.Equal(bots, results.Count(r => r));
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task HostileGarbageFlood_ListenerStaysUp_RealBotStillHandshakes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var rng = new Random(0x534);
            for (var i = 0; i < 30; i++)
            {
                using var bad = new TcpClient();
                await bad.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token);
                var junk = new byte[rng.Next(1, 256)];
                rng.NextBytes(junk);
                try { await bad.GetStream().WriteAsync(junk, cts.Token); } catch { }
            }

            // After the flood, a legitimate bot must still complete the handshake.
            var created = await bundle.Credentials.CreateAsync("good", "good", cts.Token);
            var st = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token);
            var stream = tcp.GetStream();
            var reader = new SofhFrameReader();
            await stream.WriteAsync(BuildNegotiateFrame(st.SessionId, st.CurrentVer, created.PlainToken), cts.Token);
            Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, await ReadTemplateAsync(reader, stream, cts.Token));
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }
}
