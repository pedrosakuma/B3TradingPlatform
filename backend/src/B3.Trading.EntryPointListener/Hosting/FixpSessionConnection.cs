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
    private readonly string _connectionId;
    private readonly FixpHandshakeStateMachine _sm = new();

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
        IBotSessionConnectionDirectory? connectionDirectory = null)
    {
        _tcpClient = tcpClient;
        _credentials = credentials;
        _sessions = sessions;
        _orders = orders;
        _connectionDirectory = connectionDirectory;
        _logger = logger;
        _connectionId = Guid.NewGuid().ToString("N");
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

            case NewOrderSingleData.MESSAGE_ID:
                if (_sm.State != FixpSessionState.Established) goto default;
                if (_orders is not null && _scope is not null)
                {
                    // Sub-issue #172 (F): the order adapter writes ER
                    // acks/rejects directly to the same stream the
                    // multiplexer's TryEnqueue path writes to. Take the
                    // shared write mutex so frames cannot interleave.
                    await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _orders.HandleNewOrderSingleAsync(stream, frame.Payload, _scope, ct)
                            .ConfigureAwait(false);
                    }
                    finally { _writeMutex.Release(); }
                }
                return true;

            case OrderCancelRequestData.MESSAGE_ID:
                if (_sm.State != FixpSessionState.Established) goto default;
                if (_orders is not null && _scope is not null)
                {
                    await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _orders.HandleOrderCancelRequestAsync(stream, frame.Payload, _scope, ct)
                            .ConfigureAwait(false);
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
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _tcpClient.Close(); } catch { /* ignore */ }
        _writeMutex.Dispose();
    }
}
