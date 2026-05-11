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

namespace B3.Trading.EntryPointListener.Tests.Integration;

/// <summary>
/// Sub-issue #170 (D) integration tests. Wires a real listener with the
/// in-memory credential + session registries and exercises the auth +
/// single-active enforcement paths over a TCP socket. Frames are written
/// and parsed with the same SOFH/SBE codecs the production listener uses.
/// </summary>
public class FixpListenerIntegrationTests
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
                s.AddNoopOrderPathStubs();
                s.AddEntryPointListener(config);
            })
            .Build();

        return new HostBundle(
            host,
            host.Services.GetRequiredService<FixpListenerHostedService>(),
            host.Services.GetRequiredService<InMemoryUserBotCredentialRegistry>(),
            host.Services.GetRequiredService<InMemoryUserBotSessionRegistry>());
    }

    // ─── Wire helpers ────────────────────────────────────────────────────

    private static byte[] BuildNegotiateFrame(uint sessionId, ulong sessionVerId, string token)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        if (tokenBytes.Length > 255)
            throw new ArgumentException("Token too long for single-byte length prefix.", nameof(token));

        var bodyLen = NegotiateData.BLOCK_LENGTH + 1 + tokenBytes.Length;
        var body = new byte[bodyLen];
        ref var msg = ref MemoryMarshal.AsRef<NegotiateData>(body.AsSpan(0, NegotiateData.BLOCK_LENGTH));
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        body[NegotiateData.BLOCK_LENGTH] = (byte)tokenBytes.Length;
        tokenBytes.CopyTo(body, NegotiateData.BLOCK_LENGTH + 1);

        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
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
        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
        SofhFrameWriter.WriteFrame(buf,
            (ushort)EstablishData.BLOCK_LENGTH, (ushort)EstablishData.MESSAGE_ID,
            SchemaIdV6, version: 6, body);
        return buf;
    }

    private static byte[] BuildTerminateFrame(uint sessionId, ulong sessionVerId)
    {
        var body = new byte[TerminateData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<TerminateData>(body.AsSpan());
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        msg.TerminationCode = TerminationCode.FINISHED;
        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
        SofhFrameWriter.WriteFrame(buf,
            (ushort)TerminateData.BLOCK_LENGTH, (ushort)TerminateData.MESSAGE_ID,
            SchemaIdV6, version: 0, body);
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

    // ─── Tests ───────────────────────────────────────────────────────────

    [Fact(Timeout = 10_000)]
    public async Task ValidPat_FullHandshake_HappyPath()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-1", "happy-path", cts.Token);
            var serverState = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            using var tcp = await ConnectAsync(endpoint, cts.Token);
            var stream = tcp.GetStream();
            var reader = new SofhFrameReader();

            await stream.WriteAsync(BuildNegotiateFrame(serverState.SessionId, serverState.CurrentVer, created.PlainToken), cts.Token);
            var negResp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(negResp.IsValid);
            Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, negResp.TemplateId);

            await stream.WriteAsync(BuildEstablishFrame(serverState.SessionId, serverState.CurrentVer), cts.Token);
            var estAck = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(estAck.IsValid);
            Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, estAck.TemplateId);

            await stream.WriteAsync(BuildTerminateFrame(serverState.SessionId, serverState.CurrentVer), cts.Token);
            var termAck = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(termAck.IsValid);
            Assert.Equal((ushort)TerminateData.MESSAGE_ID, termAck.TemplateId);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task UnknownPat_NegotiateRejectedWithCredentials()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            using var tcp = await ConnectAsync(endpoint, cts.Token);
            var stream = tcp.GetStream();
            var reader = new SofhFrameReader();

            await stream.WriteAsync(BuildNegotiateFrame(1, 1, "b3t_doesnotexist_zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"), cts.Token);
            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(resp.IsValid);
            Assert.Equal((ushort)NegotiateRejectData.MESSAGE_ID, resp.TemplateId);
            var rejectData = MemoryMarshal.Read<NegotiateRejectData>(resp.Payload);
            Assert.Equal(NegotiationRejectCode.CREDENTIALS, rejectData.NegotiationRejectCode);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task RevokedPat_NegotiateRejected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-r", "revoked", cts.Token);
            await bundle.Credentials.RevokeAsync("user-r", created.Credential.Id, cts.Token);

            using var tcp = await ConnectAsync(endpoint, cts.Token);
            var stream = tcp.GetStream();
            var reader = new SofhFrameReader();

            await stream.WriteAsync(BuildNegotiateFrame(1, 1, created.PlainToken), cts.Token);
            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(resp.IsValid);
            Assert.Equal((ushort)NegotiateRejectData.MESSAGE_ID, resp.TemplateId);
            var rejectData = MemoryMarshal.Read<NegotiateRejectData>(resp.Payload);
            Assert.Equal(NegotiationRejectCode.CREDENTIALS, rejectData.NegotiationRejectCode);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task SimultaneousEstablish_SecondRejectsAndBumpsVersion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-a", "single-active", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            // First connection establishes and stays connected.
            using var first = await ConnectAsync(endpoint, cts.Token);
            var firstStream = first.GetStream();
            var firstReader = new SofhFrameReader();
            await firstStream.WriteAsync(BuildNegotiateFrame(state.SessionId, state.CurrentVer, created.PlainToken), cts.Token);
            var firstNeg = await ReadFrameAsync(firstReader, firstStream, cts.Token);
            Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, firstNeg.TemplateId);
            await firstStream.WriteAsync(BuildEstablishFrame(state.SessionId, state.CurrentVer), cts.Token);
            var firstEst = await ReadFrameAsync(firstReader, firstStream, cts.Token);
            Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, firstEst.TemplateId);

            // Second connection with the same (still-current) ver must be rejected
            // with SESSION_BLOCKED, and the server-side ver must advance.
            using var second = await ConnectAsync(endpoint, cts.Token);
            var secondStream = second.GetStream();
            var secondReader = new SofhFrameReader();
            await secondStream.WriteAsync(BuildNegotiateFrame(state.SessionId, state.CurrentVer, created.PlainToken), cts.Token);
            var secondNeg = await ReadFrameAsync(secondReader, secondStream, cts.Token);
            Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, secondNeg.TemplateId);
            await secondStream.WriteAsync(BuildEstablishFrame(state.SessionId, state.CurrentVer), cts.Token);
            var secondEst = await ReadFrameAsync(secondReader, secondStream, cts.Token);
            Assert.Equal((ushort)EstablishRejectData.MESSAGE_ID, secondEst.TemplateId);
            var secondReject = MemoryMarshal.Read<EstablishRejectData>(secondEst.Payload);
            Assert.Equal(EstablishRejectCode.SESSION_BLOCKED, secondReject.EstablishmentRejectCode);
            // Reject must carry the post-bump ver so the squatter can
            // resync without a fresh Negotiate (RFC §4.5/§4.8).
            Assert.Equal((ulong)(SessionVerID)(state.CurrentVer + 1), (ulong)secondReject.SessionVerID);

            // Server advanced the version durably (RFC §4.8 fence) before sending the reject.
            var afterBump = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);
            Assert.Equal(state.CurrentVer + 1, afterBump.CurrentVer);

            // A third attempt from yet another connection that still uses the
            // pre-bump ver also fails — but with INVALID_SESSIONVERID this time
            // (the version is now stale, not duplicate).
            using var third = await ConnectAsync(endpoint, cts.Token);
            var thirdStream = third.GetStream();
            var thirdReader = new SofhFrameReader();
            await thirdStream.WriteAsync(BuildNegotiateFrame(state.SessionId, state.CurrentVer, created.PlainToken), cts.Token);
            _ = await ReadFrameAsync(thirdReader, thirdStream, cts.Token);
            await thirdStream.WriteAsync(BuildEstablishFrame(state.SessionId, state.CurrentVer), cts.Token);
            var thirdEst = await ReadFrameAsync(thirdReader, thirdStream, cts.Token);
            Assert.Equal((ushort)EstablishRejectData.MESSAGE_ID, thirdEst.TemplateId);
            var thirdReject = MemoryMarshal.Read<EstablishRejectData>(thirdEst.Payload);
            Assert.Equal(EstablishRejectCode.INVALID_SESSIONVERID, thirdReject.EstablishmentRejectCode);
            // Stale-ver reject also echoes the server-current ver so the
            // bot can resync. After the bump, that's `state.CurrentVer + 1`.
            Assert.Equal((ulong)(SessionVerID)(state.CurrentVer + 1), (ulong)thirdReject.SessionVerID);

            // Stale-ver path must NOT have bumped again.
            var afterStale = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);
            Assert.Equal(afterBump.CurrentVer, afterStale.CurrentVer);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task StaleSessionVerId_EstablishRejected_NoBump()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-s", "stale", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            using var tcp = await ConnectAsync(endpoint, cts.Token);
            var stream = tcp.GetStream();
            var reader = new SofhFrameReader();

            // Both Negotiate and Establish carry the same stale ver so the
            // shape FSM (Negotiate↔Establish must match) passes; the registry
            // check then sees a ver that does not match the server-side
            // current ver and must reject with INVALID_SESSIONVERID without
            // bumping.
            var staleVer = state.CurrentVer + 99;
            await stream.WriteAsync(BuildNegotiateFrame(state.SessionId, staleVer, created.PlainToken), cts.Token);
            _ = await ReadFrameAsync(reader, stream, cts.Token);
            await stream.WriteAsync(BuildEstablishFrame(state.SessionId, staleVer), cts.Token);
            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(resp.IsValid);
            Assert.Equal((ushort)EstablishRejectData.MESSAGE_ID, resp.TemplateId);
            var reject = MemoryMarshal.Read<EstablishRejectData>(resp.Payload);
            Assert.Equal(EstablishRejectCode.INVALID_SESSIONVERID, reject.EstablishmentRejectCode);

            var after = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);
            Assert.Equal(state.CurrentVer, after.CurrentVer);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task ReconnectAfterTerminate_Succeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-rc", "reconnect", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            // First connection: full handshake then Terminate.
            using (var first = await ConnectAsync(endpoint, cts.Token))
            {
                var s = first.GetStream();
                var r = new SofhFrameReader();
                await s.WriteAsync(BuildNegotiateFrame(state.SessionId, state.CurrentVer, created.PlainToken), cts.Token);
                _ = await ReadFrameAsync(r, s, cts.Token);
                await s.WriteAsync(BuildEstablishFrame(state.SessionId, state.CurrentVer), cts.Token);
                var ack = await ReadFrameAsync(r, s, cts.Token);
                Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, ack.TemplateId);
                await s.WriteAsync(BuildTerminateFrame(state.SessionId, state.CurrentVer), cts.Token);
                _ = await ReadFrameAsync(r, s, cts.Token);
            }

            // Allow the listener's release-on-close path to run before
            // the next connection attempts to claim the slot.
            await WaitForSlotReleaseAsync(bundle.Sessions, created.Credential.Id, state.CurrentVer, cts.Token);

            using var second = await ConnectAsync(endpoint, cts.Token);
            var s2 = second.GetStream();
            var r2 = new SofhFrameReader();
            await s2.WriteAsync(BuildNegotiateFrame(state.SessionId, state.CurrentVer, created.PlainToken), cts.Token);
            _ = await ReadFrameAsync(r2, s2, cts.Token);
            await s2.WriteAsync(BuildEstablishFrame(state.SessionId, state.CurrentVer), cts.Token);
            var ack2 = await ReadFrameAsync(r2, s2, cts.Token);
            Assert.True(ack2.IsValid);
            Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, ack2.TemplateId);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task TruncatedNegotiateFrame_ServerTerminates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            using var tcp = await ConnectAsync(endpoint, cts.Token);
            var stream = tcp.GetStream();
            var reader = new SofhFrameReader();

            var truncatedBody = new byte[NegotiateData.BLOCK_LENGTH - 1];
            var frameSize = SofhFrameWriter.FrameSize(truncatedBody.Length);
            var buf = new byte[frameSize];
            SofhFrameWriter.WriteFrame(buf,
                (ushort)(NegotiateData.BLOCK_LENGTH - 1), (ushort)NegotiateData.MESSAGE_ID,
                SchemaIdV6, version: 6, truncatedBody);
            await stream.WriteAsync(buf, cts.Token);

            var response = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.True(response.IsValid);
            Assert.Equal((ushort)TerminateData.MESSAGE_ID, response.TemplateId);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    /// <summary>
    /// Polls until the single-active slot is released. The release path
    /// runs in the listener's connection task <c>finally</c> block, which
    /// is concurrent with the test's next TCP connect — without this
    /// barrier the reconnect can race the release and see a stale claim.
    /// </summary>
    private static async Task WaitForSlotReleaseAsync(
        InMemoryUserBotSessionRegistry sessions, Guid credentialId, ulong attemptedVer, CancellationToken ct)
    {
        var probe = $"probe-{Guid.NewGuid():N}";
        for (var i = 0; i < 200; i++)
        {
            if (await sessions.TryClaimActiveAsync(credentialId, attemptedVer, probe, ct).ConfigureAwait(false))
            {
                await sessions.ReleaseAsync(credentialId, probe, ct).ConfigureAwait(false);
                return;
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Single-active slot never released after Terminate.");
    }
}
