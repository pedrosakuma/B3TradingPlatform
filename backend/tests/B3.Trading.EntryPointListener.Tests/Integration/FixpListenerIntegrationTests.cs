using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Tests.Integration;

public class FixpListenerIntegrationTests
{
    private static IHost BuildHost(out FixpListenerHostedService listenerService)
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
                s.AddEntryPointListener(config);
            })
            .Build();

        listenerService = host.Services.GetRequiredService<FixpListenerHostedService>();
        return host;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static async Task SendFrameAsync(
        NetworkStream stream,
        ushort blockLength,
        ushort templateId,
        ushort schemaId,
        ushort version,
        byte[] body,
        CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
        SofhFrameWriter.WriteFrame(buf, blockLength, templateId, schemaId, version, body);
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    private readonly record struct FrameData(bool IsValid, ushort TemplateId, byte[] Payload);

    private static async Task<FrameData> ReadFrameAsync(
        SofhFrameReader reader,
        NetworkStream stream,
        CancellationToken ct)
    {
        var buf = new byte[4096];
        while (true)
        {
            if (reader.TryReadFrame(out var frame))
                return new FrameData(true, frame.TemplateId, frame.Payload.ToArray());
            if (reader.HasProtocolError) return default;

            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return default;
            reader.Append(buf.AsSpan(0, n));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task FullHandshake_NegotiateEstablishTerminate_HappyPath()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        using var host = BuildHost(out var listenerService);
        await host.StartAsync(cts.Token);

        var endpoint = await listenerService.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token);
        var stream = tcpClient.GetStream();
        var reader = new SofhFrameReader();

        const uint SessionId = 0xDEADBEEF;
        const ulong SessionVerId = 1;

        // ── Negotiate ──────────────────────────────────────────────────────────
        var negBody = new byte[NegotiateData.BLOCK_LENGTH];
        ref var neg = ref MemoryMarshal.AsRef<NegotiateData>(negBody.AsSpan());
        neg.SessionID = (SessionID)SessionId;
        neg.SessionVerID = (SessionVerID)SessionVerId;

        await SendFrameAsync(stream,
            (ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID,
            1, 6, negBody, cts.Token);

        var negRespFrame = await ReadFrameAsync(reader, stream, cts.Token);
        Assert.True(negRespFrame.IsValid);
        Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, negRespFrame.TemplateId);
        var negRespData = MemoryMarshal.Read<NegotiateResponseData>(negRespFrame.Payload);
        Assert.Equal(SessionId, (uint)negRespData.SessionID);
        Assert.Equal(SessionVerId, (ulong)negRespData.SessionVerID);

        // ── Establish ─────────────────────────────────────────────────────────
        var estBody = new byte[EstablishData.BLOCK_LENGTH];
        ref var est = ref MemoryMarshal.AsRef<EstablishData>(estBody.AsSpan());
        est.SessionID = (SessionID)SessionId;
        est.SessionVerID = (SessionVerID)SessionVerId;

        await SendFrameAsync(stream,
            (ushort)EstablishData.BLOCK_LENGTH, (ushort)EstablishData.MESSAGE_ID,
            1, 6, estBody, cts.Token);

        var estAckFrame = await ReadFrameAsync(reader, stream, cts.Token);
        Assert.True(estAckFrame.IsValid);
        Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, estAckFrame.TemplateId);
        var estAckData = MemoryMarshal.Read<EstablishAckData>(estAckFrame.Payload);
        Assert.Equal(SessionId, (uint)estAckData.SessionID);

        // ── Terminate ─────────────────────────────────────────────────────────
        var termBody = new byte[TerminateData.BLOCK_LENGTH];
        ref var term = ref MemoryMarshal.AsRef<TerminateData>(termBody.AsSpan());
        term.SessionID = (SessionID)SessionId;
        term.SessionVerID = (SessionVerID)SessionVerId;
        term.TerminationCode = TerminationCode.FINISHED;

        await SendFrameAsync(stream,
            (ushort)TerminateData.BLOCK_LENGTH, (ushort)TerminateData.MESSAGE_ID,
            1, 0, termBody, cts.Token);

        var echoTermFrame = await ReadFrameAsync(reader, stream, cts.Token);
        Assert.True(echoTermFrame.IsValid);
        Assert.Equal((ushort)TerminateData.MESSAGE_ID, echoTermFrame.TemplateId);

        await host.StopAsync(cts.Token);
    }

    [Fact(Timeout = 10_000)]
    public async Task TruncatedNegotiateFrame_ServerTerminates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        using var host = BuildHost(out var listenerService);
        await host.StartAsync(cts.Token);

        var endpoint = await listenerService.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token);
        var stream = tcpClient.GetStream();
        var reader = new SofhFrameReader();

        // Send a Negotiate frame whose body is 1 byte shorter than BLOCK_LENGTH.
        var truncatedBody = new byte[NegotiateData.BLOCK_LENGTH - 1];
        await SendFrameAsync(stream,
            (ushort)(NegotiateData.BLOCK_LENGTH - 1), (ushort)NegotiateData.MESSAGE_ID,
            1, 6, truncatedBody, cts.Token);

        var response = await ReadFrameAsync(reader, stream, cts.Token);
        Assert.True(response.IsValid, "Server should respond to a truncated frame");
        Assert.Equal((ushort)TerminateData.MESSAGE_ID, response.TemplateId);

        await host.StopAsync(cts.Token);
    }
}

