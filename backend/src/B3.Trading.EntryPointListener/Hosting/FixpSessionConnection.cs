using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Handshake;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Manages the FIXP session lifecycle for a single accepted TCP
/// connection. Reads SOFH-framed SBE messages, drives
/// <see cref="FixpHandshakeStateMachine"/>, and writes responses.
///
/// <para>Sub-issue #170 (<c>D</c>): authenticates the
/// <c>Negotiate.Credentials</c> buffer against
/// <see cref="IUserBotCredentialRegistry"/>, attaches the resulting
/// <see cref="FixpConnectionScope"/> to the connection, and on
/// <c>Establish</c> claims the per-credential session via
/// <see cref="IUserBotSessionRegistry"/> with single-active enforcement.
/// </para>
/// </summary>
internal sealed class FixpSessionConnection : IBotSessionOutboundSender, IDisposable
{
    private const ushort SchemaIdV6 = 1;
    private const ushort VersionNegotiateResponse = 6;
    private const ushort VersionNegotiateReject = 6;
    private const ushort VersionEstablishAck = 6;
    private const ushort VersionEstablishReject = 6;
    private const ushort VersionTerminate = 0;

    /// <summary>
    /// Hard ceiling on the credential buffer length we will UTF-8 decode.
    /// The PAT shape is ≤ 50 bytes (<c>b3t_</c> + 10 short-id + <c>_</c> +
    /// 32 base64url secret); we stay generous to absorb future format
    /// growth without lifting the cap into config. Anything larger is
    /// rejected without allocation as a malformed buffer.
    /// </summary>
    private const int MaxCredentialBufferBytes = 256;

    private readonly TcpClient _tcpClient;
    private readonly ILogger _logger;
    private readonly IUserBotCredentialRegistry _credentials;
    private readonly IUserBotSessionRegistry _sessions;
    private readonly FixpOrderAdapter? _orders;
    private readonly IBotSessionConnectionDirectory? _connectionDirectory;
    private readonly BotOutboundCoordinator? _outboundCoordinator;
    private readonly EntryPointListenerOptions _options;
    private readonly TimeProvider _clock;
    private readonly string _connectionId;
    private readonly FixpHandshakeStateMachine _sm = new();

    // Sub-issue #173 (G). Inbound/outbound seq state.
    //
    // _nextExpectedInboundSeq is the SeqNum the listener expects on the
    // BusinessHeader.MsgSeqNum of the *next* application message from
    // the bot. Init=1 per FIXP convention; bumped on every successfully
    // accepted application message and on every Sequence/RetransmitRequest
    // that carries a NextSeqNo > our expected (gap detected → emit
    // NotApplied, re-sync the watermark to the bot's claimed value).
    //
    // Application messages carrying MsgSeqNum=0 are treated as "implicit"
    // by the receiver and consume the expected slot without strict
    // validation — a mode kept for backward compatibility with the F-era
    // tests that did not stamp the business header.
    private long _nextExpectedInboundSeq = 1;
    // Tick of the most recent outbound write the connection is aware
    // of (handshake/order-ack/multiplexer push or session-control). Used
    // by the heartbeat loop to suppress a Sequence emission when the
    // bot has already observed a real frame within the cadence window.
    private long _lastOutboundTicks;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatLoop;

    // Outbound-write serialisation for the multiplexer (sub-issue F).
    // The handshake/order-ack writers and the multiplexer's enqueue
    // hand-off both lock on this when emitting bytes to the stream so
    // partial frames cannot interleave.
    private readonly SemaphoreSlim _writeMutex = new(1, 1);
    private NetworkStream? _stream;
    private volatile bool _registeredInDirectory;
    private volatile bool _closed;

    private FixpConnectionScope? _scope;
    private bool _slotClaimed;

    public FixpSessionConnection(
        TcpClient tcpClient,
        IUserBotCredentialRegistry credentials,
        IUserBotSessionRegistry sessions,
        ILogger logger,
        FixpOrderAdapter? orders = null,
        IBotSessionConnectionDirectory? connectionDirectory = null,
        BotOutboundCoordinator? outboundCoordinator = null,
        EntryPointListenerOptions? options = null,
        TimeProvider? clock = null)
    {
        _tcpClient = tcpClient;
        _credentials = credentials;
        _sessions = sessions;
        _orders = orders;
        _connectionDirectory = connectionDirectory;
        _outboundCoordinator = outboundCoordinator;
        _options = options ?? new EntryPointListenerOptions();
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _connectionId = Guid.NewGuid().ToString("N");
        _lastOutboundTicks = _clock.GetUtcNow().UtcTicks;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var client = _tcpClient;
        var stream = client.GetStream();
        _stream = stream;
        var remote = SafeRemote(client);
        var reader = new SofhFrameReader();
        var readBuf = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(readBuf, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FIXP connection read error; closing.");
                    break;
                }

                if (n == 0) break; // peer closed

                reader.Append(readBuf.AsSpan(0, n));

                if (reader.HasProtocolError)
                {
                    _logger.LogWarning("FIXP framing error: {Msg}", reader.ProtocolErrorMessage);
                    await BestEffortSendTerminateAsync(stream, TerminationCode.INVALID_SOFH, ct)
                        .ConfigureAwait(false);
                    return;
                }

                while (reader.TryReadFrame(out var frame))
                {
                    var decoded = ExtractFrame(frame);
                    var keepGoing = await HandleFrameAsync(stream, decoded, remote, ct).ConfigureAwait(false);
                    if (!keepGoing) return;
                }

                if (reader.HasProtocolError)
                {
                    await BestEffortSendTerminateAsync(stream, TerminationCode.INVALID_SOFH, ct)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }
        finally
        {
            _closed = true;
            StopHeartbeatLoop();
            ArrayPool<byte>.Shared.Return(readBuf);
            // Deregister BEFORE the stream is closed so a racing ER from
            // the multiplexer hot path either sees us in the directory
            // (and TryEnqueue takes the write mutex which we still own)
            // or sees us absent (and falls through to buffering). Either
            // outcome is safe; no NRE on a half-disposed stream.
            if (_registeredInDirectory && _scope is not null && _connectionDirectory is not null)
            {
                _connectionDirectory.Deregister(_scope.Principal.CredentialId, this);
                _registeredInDirectory = false;
            }
            // Always release any single-active slot we hold, even on
            // abrupt close — otherwise a crashed bot would lock its own
            // credential out until the next version bump.
            if (_slotClaimed && _scope is not null)
            {
                try
                {
                    await _sessions.ReleaseAsync(_scope.Principal.CredentialId, _connectionId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FIXP single-active slot release failed (best-effort).");
                }
            }
        }
    }

    /// <summary>
    /// Heap-copy of a <see cref="SofhFrame"/> so the connection's async
    /// dispatch can survive across <c>await</c>s without holding a span
    /// into the reader's rotating buffer.
    /// </summary>
    private readonly struct DecodedFrame
    {
        public ushort TemplateId { get; init; }
        public byte[] Payload { get; init; }
    }

    private static DecodedFrame ExtractFrame(SofhFrame frame) => new()
    {
        TemplateId = frame.TemplateId,
        Payload = frame.Payload.ToArray(),
    };

    /// <summary>
    /// Returns <c>true</c> to keep the connection loop running, <c>false</c>
    /// to terminate the session and close the socket.
    /// </summary>
    private async Task<bool> HandleFrameAsync(
        NetworkStream stream, DecodedFrame frame, string remote, CancellationToken ct)
    {
        switch (frame.TemplateId)
        {
            case NegotiateData.MESSAGE_ID:
                return await HandleNegotiateAsync(stream, frame, remote, ct).ConfigureAwait(false);

            case EstablishData.MESSAGE_ID:
                return await HandleEstablishAsync(stream, frame, ct).ConfigureAwait(false);

            case TerminateData.MESSAGE_ID:
                return await HandleTerminateAsync(stream, frame, ct).ConfigureAwait(false);

            case SequenceData.MESSAGE_ID:
                if (_sm.State != FixpSessionState.Established) goto default;
                await HandleInboundSequenceAsync(stream, frame, ct).ConfigureAwait(false);
                return true;

            case RetransmitRequestData.MESSAGE_ID:
                if (_sm.State != FixpSessionState.Established) goto default;
                await HandleInboundRetransmitRequestAsync(stream, frame, ct).ConfigureAwait(false);
                return true;

            case NewOrderSingleData.MESSAGE_ID:
                if (_sm.State != FixpSessionState.Established) goto default;
                if (_orders is not null && _scope is not null)
                {
                    // Sub-issue #173 (G): track inbound seq via the
                    // BusinessHeader.MsgSeqNum. Out-of-order forward
                    // (seq > expected) ⇒ NotApplied (gap signal); the
                    // current message is still processed (bot's
                    // idempotent ClOrdId flow swallows true duplicates
                    // upstream). Backward (seq < expected) ⇒ duplicate,
                    // suppress dispatch entirely so the order adapter
                    // does not double-emit a side-effect.
                    var fresh = await TrackInboundAppMessageAsync(
                        stream, frame.Payload, NewOrderSingleData.BLOCK_LENGTH, ct).ConfigureAwait(false);
                    if (!fresh) return true;

                    // Sub-issue #172 (F): the order adapter writes ER
                    // acks/rejects directly to the same stream the
                    // multiplexer's TryEnqueue path writes to. Take the
                    // shared write mutex so frames cannot interleave.
                    await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _orders.HandleNewOrderSingleAsync(stream, frame.Payload, _scope, ct)
                            .ConfigureAwait(false);
                        TouchOutbound();
                    }
                    finally { _writeMutex.Release(); }
                }
                return true;

            case OrderCancelRequestData.MESSAGE_ID:
                if (_sm.State != FixpSessionState.Established) goto default;
                if (_orders is not null && _scope is not null)
                {
                    var fresh = await TrackInboundAppMessageAsync(
                        stream, frame.Payload, OrderCancelRequestData.BLOCK_LENGTH, ct).ConfigureAwait(false);
                    if (!fresh) return true;

                    await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _orders.HandleOrderCancelRequestAsync(stream, frame.Payload, _scope, ct)
                            .ConfigureAwait(false);
                        TouchOutbound();
                    }
                    finally { _writeMutex.Release(); }
                }
                return true;

            default:
                if (_sm.State == FixpSessionState.Established)
                {
                    // Application-message paths land here; sub-issue E adds
                    // NewOrderSingle/Cancel handling. v0 ignores them.
                    return true;
                }
                var pre = _sm.OnApplicationMessageBeforeEstablished();
                await SendActionAsync(stream, pre, sessionId: 0, sessionVerId: 0, ct).ConfigureAwait(false);
                return false;
        }
    }

    // ─── Negotiate ───────────────────────────────────────────────────────

    private async Task<bool> HandleNegotiateAsync(
        NetworkStream stream, DecodedFrame frame, string remote, CancellationToken ct)
    {
        if (frame.Payload.Length < NegotiateData.BLOCK_LENGTH)
        {
            _logger.LogInformation(
                "fixp.negotiate.reject reason=INVALID_FRAME remote={Remote}", remote);
            await SendActionAsync(stream, HandshakeAction.Terminate(TerminationCode.UNSPECIFIED),
                sessionId: 0, sessionVerId: 0, ct).ConfigureAwait(false);
            return false;
        }

        var msg = MemoryMarshal.Read<NegotiateData>(frame.Payload);
        var sessionId = (uint)msg.SessionID;
        var sessionVerId = (ulong)msg.SessionVerID;

        // Drive FSM first so out-of-order Negotiates (e.g. after Establish)
        // get the right termination code regardless of credentials state.
        var fsmAction = _sm.OnNegotiate(in msg);
        if (fsmAction.Kind != HandshakeActionKind.SendNegotiateResponse)
        {
            await SendActionAsync(stream, fsmAction, sessionId, sessionVerId, ct).ConfigureAwait(false);
            return !fsmAction.IsTerminating;
        }

        // Auth: pull the Credentials var-data field from the SBE payload
        // and resolve it through the credential registry. The token itself
        // is intentionally never logged — only the public CredShortId.
        if (!TryReadCredentials(frame.Payload, out var token))
        {
            _logger.LogInformation(
                "fixp.negotiate.reject reason=CREDENTIALS detail=malformed-buffer remote={Remote}",
                remote);
            await WriteNegotiateRejectAsync(stream, sessionId, sessionVerId,
                NegotiationRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        var credential = await _credentials.TryAuthenticateAsync(token, ct).ConfigureAwait(false);
        if (credential is null)
        {
            _logger.LogInformation(
                "fixp.negotiate.reject reason=CREDENTIALS remote={Remote}",
                remote);
            await WriteNegotiateRejectAsync(stream, sessionId, sessionVerId,
                NegotiationRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        // Allocate (or load) the per-credential session state up-front so
        // Establish can validate sid/ver synchronously off the resolved
        // scope without re-querying the registry.
        var sessionState = await _sessions.GetOrCreateAsync(credential.Id, ct).ConfigureAwait(false);
        var principal = new BotSessionPrincipal(
            credential.UserId, credential.Id, credential.CredShortId, credential.Label);
        _scope = new FixpConnectionScope(_connectionId, principal, sessionState);

        _logger.LogInformation(
            "fixp.negotiate.ok credShortId={CredShortId} userId={UserId} remote={Remote} connectionId={ConnectionId}",
            credential.CredShortId, credential.UserId, remote, _connectionId);

        await WriteNegotiateResponseAsync(stream, sessionId, sessionVerId, ct).ConfigureAwait(false);
        return true;
    }

    // ─── Establish ───────────────────────────────────────────────────────

    private async Task<bool> HandleEstablishAsync(
        NetworkStream stream, DecodedFrame frame, CancellationToken ct)
    {
        if (frame.Payload.Length < EstablishData.BLOCK_LENGTH)
        {
            await SendActionAsync(stream, HandshakeAction.Terminate(TerminationCode.UNSPECIFIED),
                sessionId: 0, sessionVerId: 0, ct).ConfigureAwait(false);
            return false;
        }

        var msg = MemoryMarshal.Read<EstablishData>(frame.Payload);
        var requestedSid = (uint)msg.SessionID;
        var requestedVer = (ulong)msg.SessionVerID;

        var fsmAction = _sm.OnEstablish(in msg);
        if (fsmAction.Kind != HandshakeActionKind.SendEstablishAck)
        {
            await SendActionAsync(stream, fsmAction, requestedSid, requestedVer, ct).ConfigureAwait(false);
            return !fsmAction.IsTerminating;
        }

        // FSM said OK → registry-level checks. The scope must exist by
        // construction (Negotiated state implies a successful Negotiate
        // populated _scope); a missing scope here is a bug, not user
        // error, but we defend with a CREDENTIALS reject just in case.
        if (_scope is null)
        {
            await WriteEstablishRejectAsync(stream, requestedSid, requestedVer,
                EstablishRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        var credentialId = _scope.Principal.CredentialId;
        var serverState = await _sessions.GetOrCreateAsync(credentialId, ct).ConfigureAwait(false);

        if (requestedSid != serverState.SessionId)
        {
            _logger.LogInformation(
                "fixp.establish.reject reason=INVALID_SESSIONID credShortId={CredShortId} requested={Sid} expected={ExpectedSid}",
                _scope.Principal.CredShortId, requestedSid, serverState.SessionId);
            await WriteEstablishRejectAsync(stream, requestedSid, requestedVer,
                EstablishRejectCode.INVALID_SESSIONID, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        if (requestedVer != serverState.CurrentVer)
        {
            _logger.LogInformation(
                "fixp.establish.reject reason=INVALID_SESSIONVERID credShortId={CredShortId} requested={Ver} expected={ExpectedVer}",
                _scope.Principal.CredShortId, requestedVer, serverState.CurrentVer);
            // Echo the server-side current ver so the bot's reconnect
            // logic can resync without having to trigger a fresh
            // Negotiate purely to discover the value.
            await WriteEstablishRejectAsync(stream, requestedSid, serverState.CurrentVer,
                EstablishRejectCode.INVALID_SESSIONVERID, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        var claimed = await _sessions.TryClaimActiveAsync(credentialId, requestedVer, _connectionId, ct)
            .ConfigureAwait(false);
        if (!claimed)
        {
            // RFC §4.5 "kick the squatter": bump the version durably (the
            // BumpVersionAsync contract is "WAL append + FlushAsync fence
            // before returning") *then* send the reject. The reject must
            // carry the **new** ver so the squatter learns the value it
            // would otherwise have to discover via a fresh Negotiate; a
            // second bot attempt with the now-stale ver will fail the
            // version check above without further bumping.
            var bumpedVer = await _sessions.BumpVersionAsync(credentialId, "single-active-violation", ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "fixp.establish.reject reason=SESSION_BLOCKED credShortId={CredShortId} cause=single-active-violation oldVer={OldVer} newVer={NewVer}",
                _scope.Principal.CredShortId, requestedVer, bumpedVer);
            await WriteEstablishRejectAsync(stream, requestedSid, bumpedVer,
                EstablishRejectCode.SESSION_BLOCKED, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        _slotClaimed = true;
        _logger.LogInformation(
            "fixp.establish.ok credShortId={CredShortId} userId={UserId} sessionId={Sid} sessionVerId={Ver} connectionId={ConnectionId}",
            _scope.Principal.CredShortId, _scope.Principal.UserId,
            serverState.SessionId, serverState.CurrentVer, _connectionId);
        await WriteEstablishAckAsync(stream, requestedSid, requestedVer, ct).ConfigureAwait(false);

        // Sub-issue #172 (F): make the connection discoverable by the
        // outbound multiplexer. Registration AFTER EstablishAck is sent
        // so a racing ER cannot reach the bot before the bot's own
        // handshake completes — the bot's parser would interpret a
        // pre-Ack ER as a protocol violation and drop the session.
        if (_connectionDirectory is not null)
        {
            _connectionDirectory.Register(credentialId, this);
            _registeredInDirectory = true;
        }

        // Sub-issue #173 (G): start the heartbeat loop that emits
        // Sequence on the configured cadence whenever idle. Started AFTER
        // directory registration so the loop can read the coordinator's
        // current outbound seq without racing the first multiplexer push.
        StartHeartbeatLoop(stream);
        return true;
    }

    // ─── Terminate ───────────────────────────────────────────────────────

    private async Task<bool> HandleTerminateAsync(
        NetworkStream stream, DecodedFrame frame, CancellationToken ct)
    {
        TerminateData msg = default;
        uint sid = 0;
        ulong ver = 0;
        if (frame.Payload.Length >= TerminateData.BLOCK_LENGTH)
        {
            msg = MemoryMarshal.Read<TerminateData>(frame.Payload);
            sid = (uint)msg.SessionID;
            ver = (ulong)msg.SessionVerID;
        }

        var action = _sm.OnTerminate(in msg);
        await SendActionAsync(stream, action, sid, ver, ct).ConfigureAwait(false);
        return false; // always close after Terminate
    }

    // ─── Inbound seq tracking (sub-issue G #173) ────────────────────────

    /// <summary>
    /// Reads the FIXP <c>InboundBusinessHeader.MsgSeqNum</c> off an
    /// application-message payload and reconciles it against
    /// <see cref="_nextExpectedInboundSeq"/>. Behaviour:
    /// <list type="bullet">
    /// <item>SeqNum == 0 ⇒ "implicit" mode (legacy F-era tests). Just
    /// bump the watermark; no validation.</item>
    /// <item>SeqNum &gt; expected ⇒ gap. Emit a <c>NotApplied(from=expected,
    /// count=delta)</c> framing message to signal the bot, then re-sync
    /// the watermark to <c>seq+1</c>. The current message is still
    /// processed downstream (RFC §4.7 — idempotent flow leaves duplicate
    /// detection to ClOrdId; dropping a reachable order would be worse
    /// than telling the bot "we missed N earlier ones, here's this
    /// one").</item>
    /// <item>SeqNum &lt; expected ⇒ duplicate. Per FIXP idempotent
    /// recipient convention, log debug and ignore (the application
    /// layer's ClOrdId uniqueness check will reject any reused id with
    /// a <c>DuplicateClOrdId</c> business reject anyway).</item>
    /// <item>SeqNum == expected ⇒ in-order. Bump.</item>
    /// </list>
    /// Returns <c>true</c> when the caller should proceed with the
    /// downstream order-adapter dispatch, <c>false</c> for duplicates
    /// the caller should swallow.
    /// </summary>
    private async Task<bool> TrackInboundAppMessageAsync(
        NetworkStream stream, byte[] payload, int blockLength, CancellationToken ct)
    {
        // The InboundBusinessHeader is the first field of the message
        // block (offset 0 within the SBE block — see InboundBusinessHeader
        // and NewOrderSingleData layout). Defensive bounds check first.
        if (payload.Length < blockLength) return true;
        var header = MemoryMarshal.Read<InboundBusinessHeader>(payload);
        var seq = (ulong)header.MsgSeqNum;
        if (seq == 0)
        {
            // Implicit mode — the bot did not stamp an explicit seq. We
            // still advance the local counter so a later explicit seq is
            // compared against a sane baseline.
            Interlocked.Increment(ref _nextExpectedInboundSeq);
            return true;
        }

        var expected = (ulong)Interlocked.Read(ref _nextExpectedInboundSeq);
        if (seq == expected)
        {
            Interlocked.Increment(ref _nextExpectedInboundSeq);
            return true;
        }
        if (seq > expected)
        {
            var gap = seq - expected;
            _logger.LogInformation(
                "fixp.inbound.gap connectionId={ConnectionId} expected={Expected} received={Received} gap={Gap}",
                _connectionId, expected, seq, gap);
            await SendNotAppliedAsync(stream, expected, (uint)Math.Min(gap, uint.MaxValue), ct)
                .ConfigureAwait(false);
            // Re-sync past the gap AND the current message.
            Interlocked.Exchange(ref _nextExpectedInboundSeq, (long)(seq + 1));
            return true;
        }
        // seq < expected: duplicate. Idempotent flow: ignore.
        _logger.LogDebug(
            "fixp.inbound.duplicate connectionId={ConnectionId} expected={Expected} received={Received}",
            _connectionId, expected, seq);
        return false;
    }

    private async Task HandleInboundSequenceAsync(
        NetworkStream stream, DecodedFrame frame, CancellationToken ct)
    {
        if (frame.Payload.Length < SequenceData.BLOCK_LENGTH) return;
        var msg = MemoryMarshal.Read<SequenceData>(frame.Payload);
        var nextSeqNo = (ulong)msg.NextSeqNo;
        var expected = (ulong)Interlocked.Read(ref _nextExpectedInboundSeq);

        if (nextSeqNo > expected)
        {
            var gap = nextSeqNo - expected;
            _logger.LogInformation(
                "fixp.inbound.sequence.gap connectionId={ConnectionId} expected={Expected} botNext={BotNext} gap={Gap}",
                _connectionId, expected, nextSeqNo, gap);
            await SendNotAppliedAsync(stream, expected, (uint)Math.Min(gap, uint.MaxValue), ct)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _nextExpectedInboundSeq, (long)nextSeqNo);
        }
        // nextSeqNo == expected → in-sync, no-op.
        // nextSeqNo < expected → bot is behind; per FIXP this is a
        // protocol error but the conservative v0 behaviour is to log
        // and leave our watermark untouched (the bot will resync via a
        // subsequent message or Terminate of its own accord).
        else if (nextSeqNo < expected)
        {
            _logger.LogWarning(
                "fixp.inbound.sequence.behind connectionId={ConnectionId} expected={Expected} botNext={BotNext}",
                _connectionId, expected, nextSeqNo);
        }
    }

    private async Task HandleInboundRetransmitRequestAsync(
        NetworkStream stream, DecodedFrame frame, CancellationToken ct)
    {
        if (frame.Payload.Length < RetransmitRequestData.BLOCK_LENGTH) return;
        var msg = MemoryMarshal.Read<RetransmitRequestData>(frame.Payload);
        var sessionId = (uint)msg.SessionID;
        var requestTs = msg.Timestamp.Time;
        var fromSeq = (ulong)msg.FromSeqNo;
        var count = (uint)msg.Count;

        if (_scope is null || _outboundCoordinator is null)
        {
            // No outbound state to replay against — should not happen
            // post-Establish but defensively reject as INVALID_SESSION.
            await SendRetransmitRejectAsync(stream, sessionId, requestTs,
                RetransmitRejectCode.INVALID_SESSION, ct).ConfigureAwait(false);
            return;
        }

        if (fromSeq == 0)
        {
            await SendRetransmitRejectAsync(stream, sessionId, requestTs,
                RetransmitRejectCode.INVALID_FROMSEQNO, ct).ConfigureAwait(false);
            return;
        }
        if (count == 0)
        {
            await SendRetransmitRejectAsync(stream, sessionId, requestTs,
                RetransmitRejectCode.INVALID_COUNT, ct).ConfigureAwait(false);
            return;
        }

        var credentialId = _scope.Principal.CredentialId;
        var currentSeq = _outboundCoordinator.GetCurrentSeq(credentialId);
        // toInclusive = fromSeq + count - 1; reject when bot asks for
        // anything past what the allocator has handed out.
        var toInclusive = fromSeq + count - 1;
        if (fromSeq > currentSeq || toInclusive > currentSeq)
        {
            _logger.LogInformation(
                "fixp.retransmit.reject reason=INVALID_FROMSEQNO connectionId={ConnectionId} from={From} count={Count} current={Current}",
                _connectionId, fromSeq, count, currentSeq);
            await SendRetransmitRejectAsync(stream, sessionId, requestTs,
                RetransmitRejectCode.INVALID_FROMSEQNO, ct).ConfigureAwait(false);
            return;
        }

        var buffer = _outboundCoordinator.GetOrCreateBuffer(credentialId);
        var range = buffer.GetRange(fromSeq, toInclusive);
        var allPresent = range.Count == (int)count
                         && range[0].Seq == fromSeq
                         && range[^1].Seq == toInclusive;
        if (!allPresent || buffer.IsOverflowed)
        {
            _logger.LogInformation(
                "fixp.retransmit.reject reason=OUT_OF_RANGE connectionId={ConnectionId} from={From} count={Count} have={Have} overflowed={Overflowed}",
                _connectionId, fromSeq, count, range.Count, buffer.IsOverflowed);
            await SendRetransmitRejectAsync(stream, sessionId, requestTs,
                RetransmitRejectCode.OUT_OF_RANGE, ct).ConfigureAwait(false);
            return;
        }

        // Replay in order under the write mutex. The mutex blocks the
        // multiplexer's TryEnqueue path so a freshly-allocated live ER
        // cannot land mid-replay and break the bot's seq monotonicity.
        // The historical seqs and bytes are written as-is — no
        // re-allocation, no re-framing.
        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            var framing = OutboundSessionMessageEncoder.EncodeRetransmission(
                sessionId, requestTs, fromSeq, count);
            await stream.WriteAsync(framing, ct).ConfigureAwait(false);
            foreach (var entry in range)
            {
                await stream.WriteAsync(entry.Bytes, ct).ConfigureAwait(false);
            }
            TouchOutbound();
            _logger.LogInformation(
                "fixp.retransmit.replay connectionId={ConnectionId} from={From} count={Count}",
                _connectionId, fromSeq, count);
        }
        finally
        {
            _writeMutex.Release();
        }
    }

    private async Task SendNotAppliedAsync(
        NetworkStream stream, ulong fromSeqNo, uint count, CancellationToken ct)
    {
        var bytes = OutboundSessionMessageEncoder.EncodeNotApplied(fromSeqNo, count);
        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            TouchOutbound();
        }
        finally { _writeMutex.Release(); }
    }

    private async Task SendRetransmitRejectAsync(
        NetworkStream stream, uint sessionId, ulong requestTimestamp,
        RetransmitRejectCode code, CancellationToken ct)
    {
        var bytes = OutboundSessionMessageEncoder.EncodeRetransmitReject(
            sessionId, requestTimestamp, code);
        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            TouchOutbound();
        }
        finally { _writeMutex.Release(); }
    }

    /// <summary>
    /// Server→bot heartbeat <c>Sequence(NextSeqNo)</c> emitted on the
    /// configured cadence whenever no real outbound frame has been sent
    /// inside the last cadence window. Lets the bot detect server-side
    /// gaps (its expected vs <c>NextSeqNo</c>) and lets B3-style
    /// keepalive timers stay live during quiet periods. Disabled when
    /// <see cref="EntryPointListenerOptions.HeartbeatIntervalMs"/> ≤ 0.
    /// </summary>
    private void StartHeartbeatLoop(NetworkStream stream)
    {
        var intervalMs = _options.HeartbeatIntervalMs;
        if (intervalMs <= 0) return;
        var interval = TimeSpan.FromMilliseconds(intervalMs);
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatLoop = Task.Run(async () =>
        {
            var ct = _heartbeatCts.Token;
            try
            {
                while (!ct.IsCancellationRequested && !_closed)
                {
                    await Task.Delay(interval, _clock, ct).ConfigureAwait(false);
                    if (_closed) return;
                    var sinceLast = _clock.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastOutboundTicks);
                    if (sinceLast < interval.Ticks) continue; // piggyback: a real frame already went out
                    await SendHeartbeatSequenceAsync(stream, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "fixp.heartbeat.loop.error connectionId={ConnectionId}", _connectionId);
            }
        });
    }

    private async Task SendHeartbeatSequenceAsync(NetworkStream stream, CancellationToken ct)
    {
        if (_scope is null || _outboundCoordinator is null) return;
        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            // Re-check piggyback under the mutex: a live ER could have
            // queued and acquired the mutex while we were waiting; in
            // that case it has already advertised the latest seq and a
            // heartbeat now would be redundant (and would race the
            // current-seq read below against any not-yet-flushed ER on
            // a sibling thread).
            var interval = TimeSpan.FromMilliseconds(
                _options?.HeartbeatIntervalMs ?? 3000);
            var sinceLast = _clock.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastOutboundTicks);
            if (sinceLast < interval.Ticks) return;

            var current = _outboundCoordinator.GetCurrentSeq(_scope.Principal.CredentialId);
            var nextSeq = current + 1;
            var bytes = OutboundSessionMessageEncoder.EncodeSequence(nextSeq);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            TouchOutbound();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "fixp.heartbeat.send.error connectionId={ConnectionId}", _connectionId);
        }
        finally { _writeMutex.Release(); }
    }

    private void TouchOutbound()
        => Interlocked.Exchange(ref _lastOutboundTicks, _clock.GetUtcNow().UtcTicks);

    private void StopHeartbeatLoop()
    {
        try { _heartbeatCts?.Cancel(); } catch { /* ignore */ }
        try { _heartbeatCts?.Dispose(); } catch { /* ignore */ }
        _heartbeatCts = null;
    }

    // ─── Async response writers ──────────────────────────────────────────

    private static async Task SendActionAsync(
        NetworkStream stream,
        HandshakeAction action,
        uint sessionId,
        ulong sessionVerId,
        CancellationToken ct)
    {
        switch (action.Kind)
        {
            case HandshakeActionKind.SendNegotiateResponse:
                await WriteNegotiateResponseAsync(stream, sessionId, sessionVerId, ct).ConfigureAwait(false);
                break;
            case HandshakeActionKind.SendEstablishAck:
                await WriteEstablishAckAsync(stream, sessionId, sessionVerId, ct).ConfigureAwait(false);
                break;
            case HandshakeActionKind.AckTerminateAndClose:
                await WriteTerminateAsync(stream, sessionId, sessionVerId, TerminationCode.FINISHED, ct)
                    .ConfigureAwait(false);
                break;
            case HandshakeActionKind.Terminate:
                await WriteTerminateAsync(stream, sessionId, sessionVerId, action.TermCode, ct)
                    .ConfigureAwait(false);
                break;
            case HandshakeActionKind.NoOp:
            default:
                break;
        }
    }

    private static async Task WriteNegotiateResponseAsync(
        NetworkStream stream, uint sessionId, ulong sessionVerId, CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(NegotiateResponseData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            Span<byte> body = stackalloc byte[NegotiateResponseData.BLOCK_LENGTH];
            body.Clear();
            ref var resp = ref MemoryMarshal.AsRef<NegotiateResponseData>(body);
            resp.SessionID = (SessionID)sessionId;
            resp.SessionVerID = (SessionVerID)sessionVerId;
            SofhFrameWriter.WriteFrame(buf.AsSpan(0, frameSize),
                (ushort)NegotiateResponseData.BLOCK_LENGTH,
                (ushort)NegotiateResponseData.MESSAGE_ID,
                SchemaIdV6, VersionNegotiateResponse,
                body);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task WriteNegotiateRejectAsync(
        NetworkStream stream, uint sessionId, ulong sessionVerId,
        NegotiationRejectCode code, CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(NegotiateRejectData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            Span<byte> body = stackalloc byte[NegotiateRejectData.BLOCK_LENGTH];
            body.Clear();
            ref var resp = ref MemoryMarshal.AsRef<NegotiateRejectData>(body);
            resp.SessionID = (SessionID)sessionId;
            resp.SessionVerID = (SessionVerID)sessionVerId;
            resp.NegotiationRejectCode = code;
            SofhFrameWriter.WriteFrame(buf.AsSpan(0, frameSize),
                (ushort)NegotiateRejectData.BLOCK_LENGTH,
                (ushort)NegotiateRejectData.MESSAGE_ID,
                SchemaIdV6, VersionNegotiateReject,
                body);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task WriteEstablishAckAsync(
        NetworkStream stream, uint sessionId, ulong sessionVerId, CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(EstablishAckData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            Span<byte> body = stackalloc byte[EstablishAckData.BLOCK_LENGTH];
            body.Clear();
            ref var resp = ref MemoryMarshal.AsRef<EstablishAckData>(body);
            resp.SessionID = (SessionID)sessionId;
            resp.SessionVerID = (SessionVerID)sessionVerId;
            SofhFrameWriter.WriteFrame(buf.AsSpan(0, frameSize),
                (ushort)EstablishAckData.BLOCK_LENGTH,
                (ushort)EstablishAckData.MESSAGE_ID,
                SchemaIdV6, VersionEstablishAck,
                body);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task WriteEstablishRejectAsync(
        NetworkStream stream, uint sessionId, ulong sessionVerId,
        EstablishRejectCode code, CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(EstablishRejectData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            Span<byte> body = stackalloc byte[EstablishRejectData.BLOCK_LENGTH];
            body.Clear();
            ref var resp = ref MemoryMarshal.AsRef<EstablishRejectData>(body);
            resp.SessionID = (SessionID)sessionId;
            resp.SessionVerID = (SessionVerID)sessionVerId;
            resp.EstablishmentRejectCode = code;
            SofhFrameWriter.WriteFrame(buf.AsSpan(0, frameSize),
                (ushort)EstablishRejectData.BLOCK_LENGTH,
                (ushort)EstablishRejectData.MESSAGE_ID,
                SchemaIdV6, VersionEstablishReject,
                body);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task WriteTerminateAsync(
        NetworkStream stream,
        uint sessionId,
        ulong sessionVerId,
        TerminationCode code,
        CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(TerminateData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            Span<byte> body = stackalloc byte[TerminateData.BLOCK_LENGTH];
            body.Clear();
            ref var msg = ref MemoryMarshal.AsRef<TerminateData>(body);
            msg.SessionID = (SessionID)sessionId;
            msg.SessionVerID = (SessionVerID)sessionVerId;
            msg.TerminationCode = code;
            SofhFrameWriter.WriteFrame(buf.AsSpan(0, frameSize),
                (ushort)TerminateData.BLOCK_LENGTH,
                (ushort)TerminateData.MESSAGE_ID,
                SchemaIdV6, VersionTerminate,
                body);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task BestEffortSendTerminateAsync(
        NetworkStream stream, TerminationCode code, CancellationToken ct)
    {
        try { await WriteTerminateAsync(stream, 0, 0, code, ct).ConfigureAwait(false); }
        catch { /* best effort */ }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the <c>Credentials</c> var-data group that follows the
    /// fixed-size block of a Negotiate body. The SBE encoding for this
    /// field is <c>byte length || varData[length]</c> — a malformed,
    /// truncated, or oversized buffer fails closed (no UTF-8 decoding,
    /// no allocation of an unbounded string).
    /// </summary>
    private static bool TryReadCredentials(ReadOnlySpan<byte> payload, out string token)
    {
        token = string.Empty;
        var blockLen = NegotiateData.BLOCK_LENGTH;
        if (payload.Length < blockLen + 1) return false;

        int len = payload[blockLen];
        if (len == 0) return false;
        if (len > MaxCredentialBufferBytes) return false;
        if (payload.Length < blockLen + 1 + len) return false;

        var bytes = payload.Slice(blockLen + 1, len);
        try
        {
            token = Encoding.UTF8.GetString(bytes);
            return token.Length > 0;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string SafeRemote(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint switch
            {
                IPEndPoint ip => $"{ip.Address}:{ip.Port}",
                { } ep => ep.ToString() ?? "?",
                _ => "?",
            };
        }
        catch { return "?"; }
    }

    // ─── IBotSessionOutboundSender (sub-issue F #172) ────────────────────

    /// <summary>
    /// Synchronously enqueues outbound bytes from the multiplexer. We
    /// fire a fire-and-forget Task that takes the write mutex and writes
    /// to the underlying stream — this is the right ordering primitive
    /// because (a) the multiplexer's drain thread must not block on
    /// socket I/O, and (b) the write mutex serialises against any
    /// handshake/order-ack writes the connection's own request loop is
    /// emitting concurrently.
    /// </summary>
    bool IBotSessionOutboundSender.TryEnqueue(ReadOnlyMemory<byte> framedBytes)
    {
        if (_closed) return false;
        var stream = _stream;
        if (stream is null) return false;

        // Fire-and-forget. Errors land in the catch and quietly close
        // the connection; the read loop will observe the broken stream
        // on its next iteration and run the deregister/release path.
        _ = Task.Run(async () =>
        {
            await _writeMutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_closed) return;
                await stream.WriteAsync(framedBytes, CancellationToken.None).ConfigureAwait(false);
                TouchOutbound();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "fixp.outbound.write.error connectionId={ConnectionId}", _connectionId);
                _closed = true;
                try { stream.Close(); } catch { /* ignore */ }
            }
            finally
            {
                _writeMutex.Release();
            }
        });
        return true;
    }

    public void Dispose()
    {
        // Used by the multiplexer's overflow path to force-close.
        _closed = true;
        StopHeartbeatLoop();
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _tcpClient.Close(); } catch { /* ignore */ }
        _writeMutex.Dispose();
    }
}
