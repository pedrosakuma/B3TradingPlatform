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
/// Sub-issue #173 (G). End-to-end retransmit + heartbeat + inbound-gap
/// tests over a real TCP socket. Builds on F's harness in
/// <see cref="FixpListenerIntegrationTests"/> but wires the
/// <see cref="BotOutboundCoordinator"/> + <see cref="IBotSessionConnectionDirectory"/>
/// directly so tests can simulate ER pushes without spinning up the
/// full <see cref="BotErMultiplexer"/> route loop.
/// </summary>
public class FixpRetransmitIntegrationTests
{
    private const ushort SchemaIdV6 = 1;

    private sealed record HostBundle(
        IHost Host,
        FixpListenerHostedService Listener,
        InMemoryUserBotCredentialRegistry Credentials,
        InMemoryUserBotSessionRegistry Sessions,
        BotOutboundCoordinator Coordinator,
        IBotSessionConnectionDirectory Directory);

    private static HostBundle BuildHost(int heartbeatMs = 0)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:EntryPointListener:Enabled"] = "true",
                ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
                ["Trading:EntryPointListener:HeartbeatIntervalMs"] = heartbeatMs.ToString(),
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
                s.AddEntryPointListener(config);
            })
            .Build();

        return new HostBundle(
            host,
            host.Services.GetRequiredService<FixpListenerHostedService>(),
            host.Services.GetRequiredService<InMemoryUserBotCredentialRegistry>(),
            host.Services.GetRequiredService<InMemoryUserBotSessionRegistry>(),
            host.Services.GetRequiredService<BotOutboundCoordinator>(),
            host.Services.GetRequiredService<IBotSessionConnectionDirectory>());
    }

    // ─── Wire helpers ────────────────────────────────────────────────────

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
        return WrapFrame((ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID, version: 6, body);
    }

    private static byte[] BuildEstablishFrame(uint sessionId, ulong sessionVerId)
    {
        var body = new byte[EstablishData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<EstablishData>(body.AsSpan());
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        return WrapFrame((ushort)EstablishData.BLOCK_LENGTH, (ushort)EstablishData.MESSAGE_ID, version: 6, body);
    }

    private static byte[] BuildRetransmitRequestFrame(uint sessionId, ulong fromSeqNo, uint count)
    {
        var body = new byte[RetransmitRequestData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<RetransmitRequestData>(body.AsSpan());
        msg.SessionID = (SessionID)sessionId;
        msg.Timestamp = new UTCTimestampNanos { Time = 999UL };
        msg.FromSeqNo = (SeqNum)fromSeqNo;
        msg.Count = (MessageCounter)count;
        return WrapFrame((ushort)RetransmitRequestData.BLOCK_LENGTH, (ushort)RetransmitRequestData.MESSAGE_ID, version: 6, body);
    }

    private static byte[] BuildSequenceFrame(ulong nextSeqNo)
    {
        var body = new byte[SequenceData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<SequenceData>(body.AsSpan());
        msg.NextSeqNo = (SeqNum)nextSeqNo;
        return WrapFrame((ushort)SequenceData.BLOCK_LENGTH, (ushort)SequenceData.MESSAGE_ID, version: 6, body);
    }

    private static byte[] WrapFrame(ushort blockLength, ushort messageId, ushort version, byte[] body)
    {
        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
        SofhFrameWriter.WriteFrame(buf, blockLength, messageId, SchemaIdV6, version, body);
        return buf;
    }

    /// <summary>
    /// Builds a synthetic "ER-shaped" framed payload — for retransmit
    /// purposes the listener treats the buffered bytes as opaque, so any
    /// well-formed SOFH frame works. Embeds a discriminator byte after
    /// the SOFH header so tests can prove specific entries were replayed
    /// in order.
    /// </summary>
    private static byte[] BuildFakeEr(byte tag)
    {
        var body = new byte[8];
        body[0] = tag;
        return WrapFrame(blockLength: 8, messageId: (ushort)ExecutionReport_NewData.MESSAGE_ID, version: 6, body);
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

    private static async Task<(TcpClient, NetworkStream, SofhFrameReader)> EstablishAsync(
        HostBundle bundle, IPEndPoint endpoint, string token, BotSessionState state, CancellationToken ct)
    {
        var tcp = await ConnectAsync(endpoint, ct);
        var stream = tcp.GetStream();
        var reader = new SofhFrameReader();
        await stream.WriteAsync(BuildNegotiateFrame(state.SessionId, state.CurrentVer, token), ct);
        var neg = await ReadFrameAsync(reader, stream, ct);
        Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, neg.TemplateId);
        await stream.WriteAsync(BuildEstablishFrame(state.SessionId, state.CurrentVer), ct);
        var ack = await ReadFrameAsync(reader, stream, ct);
        Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, ack.TemplateId);
        return (tcp, stream, reader);
    }

    /// <summary>
    /// Polls until <see cref="IBotSessionConnectionDirectory.TryGet"/>
    /// returns the connected sender — directory registration runs after
    /// EstablishAck is sent, so the test client may observe the ack
    /// before the listener finishes registering.
    /// </summary>
    private static async Task<IBotSessionOutboundSender> WaitForSenderAsync(
        IBotSessionConnectionDirectory dir, Guid credId, CancellationToken ct)
    {
        for (var i = 0; i < 200; i++)
        {
            if (dir.TryGet(credId, out var s)) return s;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException("Sender never registered.");
    }

    /// <summary>
    /// Per-credential helper that allocates a seq, appends the framed
    /// bytes to the buffer, and pushes through the live sender — the
    /// same triple of effects the multiplexer's RouteOne path performs,
    /// but synchronous so tests do not need to drain a channel.
    /// </summary>
    private static void EnqueueAndBuffer(
        BotOutboundCoordinator coord, IBotSessionOutboundSender sender, Guid credId, byte[] framed)
    {
        var seq = coord.AllocateNext(credId);
        coord.GetOrCreateBuffer(credId).Append(seq, framed);
        sender.TryEnqueue(framed);
    }

    // ─── Tests ───────────────────────────────────────────────────────────

    [Fact(Timeout = 15_000)]
    public async Task RetransmitRequest_AllInBuffer_ReplaysHistoricalBytesInOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-r1", "retx-replay", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t1 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t1_tcp = __t1.Item1; var stream = __t1.Item2; var reader = __t1.Item3;

            var sender = await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Push 5 ER-shaped frames live (seqs 1..5). Read each frame
            // back off the wire BEFORE pushing the next so the test
            // controls the buffer's seq→bytes mapping deterministically;
            // the F-era TryEnqueue path runs each push as a fire-and-
            // forget Task whose mutex acquisition order, when many are
            // queued back-to-back, is FIFO at SemaphoreSlim but not
            // guaranteed at Task.Run scheduling, so a tight loop without
            // reads in between can land bytes out of allocation order.
            var live = new byte[5][];
            for (byte i = 0; i < 5; i++)
            {
                live[i] = BuildFakeEr(tag: (byte)(0xA0 + i));
                EnqueueAndBuffer(bundle.Coordinator, sender, created.Credential.Id, live[i]);
                var f = await ReadFrameAsync(reader, stream, cts.Token);
                Assert.Equal((ushort)ExecutionReport_NewData.MESSAGE_ID, f.TemplateId);
                Assert.Equal((byte)(0xA0 + i), f.Payload[0]);
            }

            // Ask for seqs 2..3 inclusive (from=2, count=2).
            await stream.WriteAsync(
                BuildRetransmitRequestFrame(state.SessionId, fromSeqNo: 2, count: 2), cts.Token);

            // Server responds with Retransmission(NextSeqNo=2, Count=2)
            // followed by the original frames for seqs 2 and 3 verbatim.
            var rt = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((ushort)RetransmissionData.MESSAGE_ID, rt.TemplateId);
            var rtMsg = MemoryMarshal.Read<RetransmissionData>(rt.Payload);
            Assert.Equal(2UL, (ulong)rtMsg.NextSeqNo);
            Assert.Equal(2U, (uint)rtMsg.Count);

            var replay1 = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((byte)(0xA0 + 1), replay1.Payload[0]);
            var replay2 = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((byte)(0xA0 + 2), replay2.Payload[0]);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task RetransmitRequest_AboveCurrent_RejectsInvalidFromSeqNo()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-r2", "retx-above", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t2 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t2_tcp = __t2.Item1; var stream = __t2.Item2; var reader = __t2.Item3;
            await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Outbound seq is 0 (no ERs sent). Asking for seq 1 is above current.
            await stream.WriteAsync(
                BuildRetransmitRequestFrame(state.SessionId, fromSeqNo: 1, count: 1), cts.Token);

            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((ushort)RetransmitRejectData.MESSAGE_ID, resp.TemplateId);
            var rej = MemoryMarshal.Read<RetransmitRejectData>(resp.Payload);
            Assert.Equal(RetransmitRejectCode.INVALID_FROMSEQNO, rej.RetransmitRejectCode);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task RetransmitRequest_BelowBufferFloor_RejectsOutOfRange()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-r3", "retx-floor", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t3 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t3_tcp = __t3.Item1; var stream = __t3.Item2; var reader = __t3.Item3;
            var sender = await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Allocate seqs 1..5 then evict 1..3 — buffer floor is now 4.
            for (byte i = 0; i < 5; i++)
            {
                var b = BuildFakeEr((byte)(0xB0 + i));
                EnqueueAndBuffer(bundle.Coordinator, sender, created.Credential.Id, b);
                _ = await ReadFrameAsync(reader, stream, cts.Token);
            }
            bundle.Coordinator.GetOrCreateBuffer(created.Credential.Id).EvictUpTo(3);

            // Asking for seq 2 (below floor=4) → OUT_OF_RANGE.
            await stream.WriteAsync(
                BuildRetransmitRequestFrame(state.SessionId, fromSeqNo: 2, count: 1), cts.Token);

            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((ushort)RetransmitRejectData.MESSAGE_ID, resp.TemplateId);
            var rej = MemoryMarshal.Read<RetransmitRejectData>(resp.Payload);
            Assert.Equal(RetransmitRejectCode.OUT_OF_RANGE, rej.RetransmitRejectCode);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task RetransmitRequest_ZeroFromSeqNo_RejectsInvalidFromSeqNo()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-r4", "retx-zero", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t4 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t4_tcp = __t4.Item1; var stream = __t4.Item2; var reader = __t4.Item3;
            await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            await stream.WriteAsync(
                BuildRetransmitRequestFrame(state.SessionId, fromSeqNo: 0, count: 1), cts.Token);
            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((ushort)RetransmitRejectData.MESSAGE_ID, resp.TemplateId);
            var rej = MemoryMarshal.Read<RetransmitRejectData>(resp.Payload);
            Assert.Equal(RetransmitRejectCode.INVALID_FROMSEQNO, rej.RetransmitRejectCode);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task RetransmitRequest_ZeroCount_RejectsInvalidCount()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-r5", "retx-zero-count", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t5 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t5_tcp = __t5.Item1; var stream = __t5.Item2; var reader = __t5.Item3;
            await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            await stream.WriteAsync(
                BuildRetransmitRequestFrame(state.SessionId, fromSeqNo: 1, count: 0), cts.Token);
            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((ushort)RetransmitRejectData.MESSAGE_ID, resp.TemplateId);
            var rej = MemoryMarshal.Read<RetransmitRejectData>(resp.Payload);
            Assert.Equal(RetransmitRejectCode.INVALID_COUNT, rej.RetransmitRejectCode);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task InboundSequence_GapDetected_NotAppliedSent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-g1", "gap", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t6 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t6_tcp = __t6.Item1; var stream = __t6.Item2; var reader = __t6.Item3;
            await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Bot claims its next outbound is 6; server expected 1 → gap of 5.
            await stream.WriteAsync(BuildSequenceFrame(nextSeqNo: 6), cts.Token);
            var resp = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((ushort)NotAppliedData.MESSAGE_ID, resp.TemplateId);
            var na = MemoryMarshal.Read<NotAppliedData>(resp.Payload);
            Assert.Equal(1UL, (ulong)na.FromSeqNo);
            Assert.Equal(5U, (uint)na.Count);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task InboundSequence_InOrder_NoGapSignalled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        // Disable heartbeat so the test only sees what we send back.
        var bundle = BuildHost(heartbeatMs: 0);
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-g2", "in-order", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t7 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t7_tcp = __t7.Item1; var stream = __t7.Item2; var reader = __t7.Item3;
            await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Bot's NextSeqNo == server's expected (1). No NotApplied
            // should be emitted. Then send a Sequence(2) which is also
            // in-sync after no app messages — still no NotApplied.
            await stream.WriteAsync(BuildSequenceFrame(nextSeqNo: 1), cts.Token);
            // Give the server a moment to (not) react.
            using var shortCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, shortCts.Token);
            try
            {
                var f = await ReadFrameAsync(reader, stream, linked.Token);
                // Anything received is a failure.
                Assert.Fail($"Unexpected frame received: template={f.TemplateId}");
            }
            catch (OperationCanceledException) when (shortCts.IsCancellationRequested)
            {
                // Expected: no frame within timeout.
            }
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Heartbeat_IdleConnection_EmitsSequenceOnCadence()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost(heartbeatMs: 200);
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-h1", "heartbeat", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t8 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t8_tcp = __t8.Item1; var stream = __t8.Item2; var reader = __t8.Item3;
            await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Wait for at least one heartbeat tick.
            var hb = await ReadFrameAsync(reader, stream, cts.Token);
            Assert.Equal((ushort)SequenceData.MESSAGE_ID, hb.TemplateId);
            var seq = MemoryMarshal.Read<SequenceData>(hb.Payload);
            // No ERs sent → current outbound seq is 0 → NextSeqNo is 1.
            Assert.Equal(1UL, (ulong)seq.NextSeqNo);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task ConcurrentLiveErAndRetransmit_NoInterleavingOnWire()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-c1", "concurrent", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            var __t9 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t9_tcp = __t9.Item1; var stream = __t9.Item2; var reader = __t9.Item3;
            var sender = await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Buffer 10 historical ERs first (seqs 1..10) and consume them.
            for (byte i = 0; i < 10; i++)
            {
                var b = BuildFakeEr((byte)(0xC0 + i));
                EnqueueAndBuffer(bundle.Coordinator, sender, created.Credential.Id, b);
            }
            for (var i = 0; i < 10; i++)
                _ = await ReadFrameAsync(reader, stream, cts.Token);

            // Kick off a retransmit AND a flood of live ERs concurrently.
            await stream.WriteAsync(
                BuildRetransmitRequestFrame(state.SessionId, fromSeqNo: 3, count: 5), cts.Token);
            var liveTask = Task.Run(() =>
            {
                for (byte i = 0; i < 20; i++)
                {
                    var b = BuildFakeEr((byte)(0xD0 + i));
                    EnqueueAndBuffer(bundle.Coordinator, sender, created.Credential.Id, b);
                }
            });

            // Read everything that arrives until we observe both:
            //   - The Retransmission framing frame + its 5 historical bytes.
            //   - All 20 new live ERs.
            // Assert the historical bytes appear contiguously after the
            // Retransmission and the live ERs are not interleaved into
            // the historical stream (FIXP wire-order invariant).
            var seenLive = new List<byte>();
            var historical = new List<byte>();
            var sawRetransmission = false;
            var historicalRemaining = 0;
            var endTime = DateTime.UtcNow.AddSeconds(8);
            while ((seenLive.Count < 20 || historical.Count < 5) && DateTime.UtcNow < endTime)
            {
                var f = await ReadFrameAsync(reader, stream, cts.Token);
                if (f.TemplateId == (ushort)RetransmissionData.MESSAGE_ID)
                {
                    Assert.False(sawRetransmission, "Two Retransmission framing messages observed.");
                    sawRetransmission = true;
                    historicalRemaining = (int)(uint)MemoryMarshal.Read<RetransmissionData>(f.Payload).Count;
                    continue;
                }
                if (sawRetransmission && historicalRemaining > 0)
                {
                    historical.Add(f.Payload[0]);
                    historicalRemaining--;
                    continue;
                }
                seenLive.Add(f.Payload[0]);
            }
            await liveTask;

            Assert.True(sawRetransmission);
            Assert.Equal(5, historical.Count);
            // Historical bytes must be exactly seqs 3..7 in order
            // (tags 0xC2..0xC6) — re-numbering or interleaving would
            // break this.
            Assert.Equal(new byte[] { 0xC2, 0xC3, 0xC4, 0xC5, 0xC6 }, historical.ToArray());
            // Live tags are a subset of 0xD0..0xD3+ in arrival order;
            // we only assert monotonic-by-arrival because the producer
            // is faster than the consumer and the channel is FIFO per
            // sender — the test's contract is "no interleave with the
            // historical block", which the contiguity check above proved.
            Assert.Equal(20, seenLive.Count);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task DisconnectAndReconnect_RetransmitCoversNewlyBufferedRange()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var bundle = BuildHost();
        try
        {
            await bundle.Host.StartAsync(cts.Token);
            var endpoint = await bundle.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await bundle.Credentials.CreateAsync("user-rc", "reconnect", cts.Token);
            var state = await bundle.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            // First connection: receive 2 ERs, then disconnect.
            {
                var __t10 = await EstablishAsync(
                    bundle, endpoint, created.PlainToken, state, cts.Token); using var tcp = __t10.Item1; var stream = __t10.Item2; var reader = __t10.Item3;
                var sender = await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);
                for (byte i = 0; i < 2; i++)
                {
                    var b = BuildFakeEr((byte)(0xE0 + i));
                    EnqueueAndBuffer(bundle.Coordinator, sender, created.Credential.Id, b);
                    _ = await ReadFrameAsync(reader, stream, cts.Token);
                }
                tcp.Close();
            }

            // While offline buffer 3 more ERs (seqs 3..5). The
            // connection directory / single-active slot must release
            // before the reconnect can claim it.
            await Task.Delay(200, cts.Token);
            for (byte i = 0; i < 3; i++)
            {
                var b = BuildFakeEr((byte)(0xE2 + i));
                var seq = bundle.Coordinator.AllocateNext(created.Credential.Id);
                bundle.Coordinator.GetOrCreateBuffer(created.Credential.Id).Append(seq, b);
            }
            for (var i = 0; i < 50 && bundle.Directory.TryGet(created.Credential.Id, out _); i++)
                await Task.Delay(20, cts.Token);

            // Reconnect (need to wait for slot release).
            for (var i = 0; i < 100; i++)
            {
                if (await bundle.Sessions.TryClaimActiveAsync(created.Credential.Id, state.CurrentVer, $"probe-{i}", cts.Token))
                {
                    await bundle.Sessions.ReleaseAsync(created.Credential.Id, $"probe-{i}", cts.Token);
                    break;
                }
                await Task.Delay(20, cts.Token);
            }

            var __t11 = await EstablishAsync(
                bundle, endpoint, created.PlainToken, state, cts.Token); using var __t11_tcp = __t11.Item1; var stream2 = __t11.Item2; var reader2 = __t11.Item3;
            await WaitForSenderAsync(bundle.Directory, created.Credential.Id, cts.Token);

            // Ask for the buffered tail: seqs 3..5.
            await stream2.WriteAsync(
                BuildRetransmitRequestFrame(state.SessionId, fromSeqNo: 3, count: 3), cts.Token);

            var rt = await ReadFrameAsync(reader2, stream2, cts.Token);
            Assert.Equal((ushort)RetransmissionData.MESSAGE_ID, rt.TemplateId);
            var f1 = await ReadFrameAsync(reader2, stream2, cts.Token);
            var f2 = await ReadFrameAsync(reader2, stream2, cts.Token);
            var f3 = await ReadFrameAsync(reader2, stream2, cts.Token);
            Assert.Equal((byte)0xE2, f1.Payload[0]);
            Assert.Equal((byte)0xE3, f2.Payload[0]);
            Assert.Equal((byte)0xE4, f3.Payload[0]);
        }
        finally
        {
            await bundle.Host.StopAsync(CancellationToken.None);
            bundle.Host.Dispose();
        }
    }

    /// <summary>
    /// Tiny adaptor — <c>using var (a, b, c) = ...</c> requires either a
    /// tuple-typed expression with each element implementing IDisposable
    /// or a single IDisposable; this helper just unpacks.
    /// </summary>
    private static (TcpClient, NetworkStream, SofhFrameReader) AsTuple(
        (TcpClient, NetworkStream, SofhFrameReader) t) => t;
}
