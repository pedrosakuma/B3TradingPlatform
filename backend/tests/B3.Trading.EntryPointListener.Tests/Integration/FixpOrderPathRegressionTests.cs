using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Tests.Integration;

/// <summary>
/// Issue #185 regression sanity: with the listener fully wired, an
/// inbound <c>NewOrderSingle</c> reaches <see cref="FixpOrderAdapter"/>
/// rather than being silently dropped. We exercise this through the
/// observable side-effect of the adapter's earliest reject path: a
/// <c>NewOrderSingle</c> referencing an unknown <c>SecurityID</c>
/// produces a <c>BusinessMessageReject</c> on the wire.
///
/// <para>
/// This guards the inverse of the original bug: if the silent-return
/// branches in <see cref="FixpSessionConnection"/> are ever
/// reintroduced — or if the composition guard regresses and the
/// adapter is no longer constructed — the test times out waiting for
/// the BMR frame.
/// </para>
/// </summary>
public class FixpOrderPathRegressionTests
{
    private const ushort SchemaIdV6 = 1;

    private sealed record HostBundle(
        IHost Host,
        FixpListenerHostedService Listener,
        InMemoryUserBotCredentialRegistry Credentials,
        InMemoryUserBotSessionRegistry Sessions);

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
                s.AddSingleton<IUserBotCredentialRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotCredentialRegistry>());
                s.AddSingleton<InMemoryUserBotSessionRegistry>();
                s.AddSingleton<IUserBotSessionRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotSessionRegistry>());

                // Real (empty) SymbolDirectory so the adapter can run
                // up to the UnknownSecurity reject. The submit/cancel
                // services are not invoked on this path; null-returning
                // factories make IServiceProviderIsService report them
                // as registered without us having to spin up the heavy
                // Application graph. The FixpOrderAdapter ctor will see
                // them as null only if the listener short-circuits
                // construction — but since SymbolDirectory is real, the
                // adapter constructor receives a real symbol directory
                // and null submit/cancel; that is fine because the
                // unknown-security path returns before either is touched.
                s.AddSingleton(new SymbolDirectory(new SymbolDirectoryOptions()));
                s.AddSingleton<OrderSubmissionService>(_ => null!);
                s.AddSingleton<OrderCancelService>(_ => null!);

                s.AddEntryPointListener(config);
            })
            .Build();

        return new HostBundle(
            host,
            host.Services.GetRequiredService<FixpListenerHostedService>(),
            host.Services.GetRequiredService<InMemoryUserBotCredentialRegistry>(),
            host.Services.GetRequiredService<InMemoryUserBotSessionRegistry>());
    }

    [Fact(Timeout = 15_000)]
    public async Task NewOrderSingle_ReachesAdapter_AndProducesBusinessMessageReject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-185", "regression", cts.Token);
            var serverState = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            using var tcp = await ConnectAsync(endpoint, cts.Token);
            var stream = tcp.GetStream();
            var reader = new SofhFrameReader();

            // Negotiate
            await stream.WriteAsync(
                BuildNegotiateFrame(serverState.SessionId, serverState.CurrentVer, created.PlainToken),
                cts.Token);
            var negResp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(negResp.IsValid);
            Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, negResp.TemplateId);

            // Establish
            await stream.WriteAsync(
                BuildEstablishFrame(serverState.SessionId, serverState.CurrentVer),
                cts.Token);
            var estAck = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(estAck.IsValid);
            Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, estAck.TemplateId);

            // NewOrderSingle: zeroed payload — SecurityID=0 is not in the
            // (empty) SymbolDirectory, so the adapter writes BMR(UnknownSecurity).
            await stream.WriteAsync(BuildZeroedNewOrderSingleFrame(serverState.SessionId), cts.Token);

            var bmr = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(bmr.IsValid, "Expected a BusinessMessageReject frame back; got nothing — " +
                "this is the silent-return regression from issue #185.");
            Assert.Equal((ushort)BusinessMessageRejectData.MESSAGE_ID, bmr.TemplateId);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    // ─── Wire helpers (kept local to make the regression test fully
    //     standalone — the existing FixpListenerIntegrationTests helpers
    //     are private to that class). ────────────────────────────────────

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
        SofhFrameWriter.WriteFrame(buf,
            (ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID,
            SchemaIdV6, version: 6, body);
        return buf;
    }

    private static byte[] BuildEstablishFrame(uint sessionId, ulong sessionVerId)
    {
        var body = new byte[EstablishData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<EstablishData>(body.AsSpan());
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        var buf = new byte[SofhFrameWriter.FrameSize(body.Length)];
        SofhFrameWriter.WriteFrame(buf,
            (ushort)EstablishData.BLOCK_LENGTH, (ushort)EstablishData.MESSAGE_ID,
            SchemaIdV6, version: 6, body);
        return buf;
    }

    private static byte[] BuildZeroedNewOrderSingleFrame(uint sessionId)
    {
        var body = new byte[NewOrderSingleData.BLOCK_LENGTH];
        // The InboundBusinessHeader sits at offset 0 of the SBE block.
        // Stamp a non-zero MsgSeqNum so TrackInboundAppMessage takes the
        // explicit-seq fast path and forwards to the adapter immediately.
        ref var header = ref MemoryMarshal.AsRef<InboundBusinessHeader>(body.AsSpan(0));
        header.SessionID = (SessionID)sessionId;
        header.MsgSeqNum = (SeqNum)1u;

        var buf = new byte[SofhFrameWriter.FrameSize(body.Length)];
        SofhFrameWriter.WriteFrame(buf,
            (ushort)NewOrderSingleData.BLOCK_LENGTH, (ushort)NewOrderSingleData.MESSAGE_ID,
            SchemaIdV6, version: 6, body);
        return buf;
    }

    private readonly record struct FrameData(bool IsValid, ushort TemplateId, byte[] Payload);

    private static async Task<FrameData> ReadFrameAsync(
        SofhFrameReader reader, NetworkStream stream, CancellationToken ct)
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

    private static async Task<TcpClient> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var c = new TcpClient();
        await c.ConnectAsync(endpoint.Address, endpoint.Port, ct).ConfigureAwait(false);
        return c;
    }
}
