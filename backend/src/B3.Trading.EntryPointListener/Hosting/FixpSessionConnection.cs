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
    /// </summary>
    private const int MaxCredentialBufferBytes = 256;

    private readonly TcpClient _tcpClient;
    private readonly ILogger _logger;
    private readonly IUserBotCredentialRegistry _credentials;
    private readonly IUserBotSessionRegistry _sessions;
    private readonly FixpOrderAdapter? _orders;
    private readonly IBotSessionConnectionDirectory? _connectionDirectory;
    private readonly BotOutboundCoordinator? _outboundCoordinator;
    private readonly RateLimiterRegistry? _rateLimiter;
    private readonly UserSessionCounter? _sessionCounter;
    private readonly EntryPointListenerOptions _options;
    private readonly TimeProvider _clock;
    private readonly string _connectionId;

    /// <summary>
    /// The validated client certificate captured during the TLS handshake
    /// (RFC user-bot-fixp-mtls-v0 §4.3), or null when no cert was presented
    /// (Optional mode) or mTLS is off. Consumed by the Negotiate-time
    /// per-credential thumbprint pin check (sub-issue D / #540).
    /// </summary>
    internal System.Security.Cryptography.X509Certificates.X509Certificate2? ClientCertificate
        => _clientCertificate;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2? _clientCertificate;
    private readonly FixpHandshakeStateMachine _sm = new();

    private long _nextExpectedInboundSeq = 1;
    private long _lastOutboundTicks;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatLoop;

    private readonly SemaphoreSlim _writeMutex = new(1, 1);
    private Stream? _stream;
    private FixpOutboundChannelWriter? _outboundWriter;
    private volatile bool _registeredInDirectory;
    private volatile bool _closed;
    private volatile bool _userSlotHeld;

    private FixpConnectionScope? _scope;
    private bool _slotClaimed;

    public FixpSessionConnection(
        TcpClient tcpClient,
        Stream stream,
        IUserBotCredentialRegistry credentials,
        IUserBotSessionRegistry sessions,
        ILogger logger,
        FixpOrderAdapter? orders = null,
        IBotSessionConnectionDirectory? connectionDirectory = null,
        BotOutboundCoordinator? outboundCoordinator = null,
        EntryPointListenerOptions? options = null,
        TimeProvider? clock = null,
        RateLimiterRegistry? rateLimiter = null,
        UserSessionCounter? sessionCounter = null,
        System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate = null)
    {
        _tcpClient = tcpClient;
        _stream = stream;
        _credentials = credentials;
        _sessions = sessions;
        _orders = orders;
        _connectionDirectory = connectionDirectory;
        _outboundCoordinator = outboundCoordinator;
        _rateLimiter = rateLimiter;
        _sessionCounter = sessionCounter;
        _options = options ?? new EntryPointListenerOptions();
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _clientCertificate = clientCertificate;
        _connectionId = Guid.NewGuid().ToString("N");
        _lastOutboundTicks = _clock.GetUtcNow().UtcTicks;
    }

    /// <summary>
    /// Legacy constructor for tests that don't supply a pre-wrapped stream.
    /// </summary>
    public FixpSessionConnection(
        TcpClient tcpClient,
        IUserBotCredentialRegistry credentials,
        IUserBotSessionRegistry sessions,
        ILogger logger,
        FixpOrderAdapter? orders = null,
        IBotSessionConnectionDirectory? connectionDirectory = null,
        BotOutboundCoordinator? outboundCoordinator = null,
        EntryPointListenerOptions? options = null,
        TimeProvider? clock = null,
        RateLimiterRegistry? rateLimiter = null,
        UserSessionCounter? sessionCounter = null,
        System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate = null)
        : this(tcpClient, tcpClient.GetStream(), credentials, sessions, logger,
               orders, connectionDirectory, outboundCoordinator, options, clock,
               rateLimiter, sessionCounter, clientCertificate)
    {
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var client = _tcpClient;
        var stream = _stream!;
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
                    // RFC §5.6 (P10/F6): hot message types are decoded
                    // synchronously from the framer's rotating span into
                    // a stack-resident struct *before* the dispatcher's
                    // first await — no per-frame `byte[]` survives the
                    // await. Cold types still take the legacy heap-copy
                    // path inside DispatchFrameAsync.
                    var keepGoing = await DispatchFrameAsync(stream, in frame, remote, ct).ConfigureAwait(false);
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
            StopHeartbeatLoop();
            ArrayPool<byte>.Shared.Return(readBuf);
            // RFC §5.3.2 shutdown drain: flush queued outbound frames
            // BEFORE flipping _closed / tearing down the stream so the
            // drain callback's WriteAsync calls go to a still-live
            // socket. Bounded by OutboundDrainShutdownTimeout so a
            // dead peer cannot stall connection cleanup; remaining
            // queued frames stay owned by the per-credential
            // BotOutboundBuffer (drain loop NEVER disposes — RFC §5.5)
            // and ride retransmit on the next reconnect.
            //
            // Drain BEFORE deregister: a frame the multiplexer
            // enqueued while we were still in the directory must
            // still get a chance to flush.
            var writerForDrain = _outboundWriter;
            if (writerForDrain is not null)
            {
                try
                {
                    await writerForDrain.CompleteAsync(_options.Buffers.OutboundDrainShutdownTimeout)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "fixp.outbound.drain.shutdown.error connectionId={ConnectionId}",
                        _connectionId);
                }
            }
            _closed = true;
            // Deregister BEFORE the stream is closed so a racing ER from
            // the multiplexer hot path either sees us in the directory
            // (and TryEnqueue lands in the now-completed writer, which
            // returns false → buffer-only path) or sees us absent (and
            // falls through to buffering). Either outcome is safe; no
            // NRE on a half-disposed stream.
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
            // Release per-user session counter
            if (_userSlotHeld && _scope is not null && _sessionCounter is not null)
            {
                _sessionCounter.Decrement(_scope.Principal.UserId);
            }
            if (_slotClaimed)
                FixpListenerMetrics.SessionsActive.Add(-1);
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
    /// RFC §5.6 (P10/F6) entry point. Synchronous switch over the SBE
    /// template id that:
    ///
    /// <list type="bullet">
    ///   <item>Decodes the hot message types (NewOrderSingle,
    ///     OrderCancelRequest, Sequence) directly from
    ///     <see cref="SofhFrame.Payload"/> into a stack-resident
    ///     <c>Decoded*</c> struct and forwards to the matching
    ///     zero-copy async handler — no <c>byte[]</c> allocated.</item>
    ///   <item>For the cold/error fall-through (handshake messages,
    ///     RetransmitRequest, malformed-length frames of hot types)
    ///     copies the payload through <see cref="ExtractFrame"/> and
    ///     delegates to the legacy <see cref="HandleFrameAsync"/>.</item>
    /// </list>
    ///
    /// <para>This method is intentionally non-<c>async</c> so it can
    /// receive the framer's <see cref="SofhFrame"/> ref-struct by
    /// reference. The first <c>await</c> happens inside the returned
    /// <see cref="Task{TResult}"/>, by which point the decode has
    /// already snapshotted every span field the handler needs.</para>
    /// </summary>
    private Task<bool> DispatchFrameAsync(
        Stream stream, in SofhFrame frame, string remote, CancellationToken ct)
    {
        switch (frame.TemplateId)
        {
            case NewOrderSingleData.MESSAGE_ID:
                if (_sm.State == FixpSessionState.Established
                    && InboundDecoders.TryDecodeNewOrderSingle(frame.Payload, out var nos))
                {
                    EnsureOrderAdapterWired();
                    return HandleNewOrderSingleZeroCopyAsync(stream, nos, ct);
                }
                break;

            case OrderCancelRequestData.MESSAGE_ID:
                if (_sm.State == FixpSessionState.Established
                    && InboundDecoders.TryDecodeOrderCancelRequest(frame.Payload, out var ocr))
                {
                    EnsureOrderAdapterWired();
                    return HandleOrderCancelRequestZeroCopyAsync(stream, ocr, ct);
                }
                break;

            case SequenceData.MESSAGE_ID:
                if (_sm.State == FixpSessionState.Established
                    && InboundDecoders.TryDecodeSequence(frame.Payload, out var seq))
                {
                    return HandleInboundSequenceZeroCopyKeepAliveAsync(stream, seq, ct);
                }
                break;
        }

        // Fall-through for: handshake/control messages, hot-type frames
        // received outside Established, and malformed-length hot frames
        // (the legacy adapter path emits the appropriate
        // BusinessMessageReject(InvalidShape) for the latter).
        var legacy = ExtractFrame(frame);
        return HandleFrameAsync(stream, legacy, remote, ct);
    }

    /// <summary>
    /// Issue #185 invariant: by the time we accept an
    /// application-layer <c>NewOrderSingle</c>/<c>OrderCancelRequest</c>
    /// frame the connection MUST have a fully-wired order adapter and a
    /// resolved <see cref="FixpConnectionScope"/> (set during Negotiate).
    /// In production this is enforced eagerly at host startup by
    /// <see cref="EntryPointListenerCompositionGuard"/>; if either is
    /// still null here we fail loudly rather than silently swallowing
    /// the order.
    /// </summary>
    private void EnsureOrderAdapterWired()
    {
        if (_orders is null)
        {
            throw new InvalidOperationException(
                "FIXP listener received an application order frame but the order " +
                "adapter is not wired. EntryPointListenerCompositionGuard should " +
                "have rejected host startup; this indicates a test harness that " +
                "enabled the listener without registering the order-path " +
                "dependencies (SymbolDirectory, OrderSubmissionService, " +
                "OrderCancelService, IUserBotOrderMappingRegistry).");
        }
        if (_scope is null)
        {
            throw new InvalidOperationException(
                "FIXP listener received an application order frame in Established " +
                "state but the connection scope is null. This violates the " +
                "Negotiate→Establish ordering invariant.");
        }
    }

    /// <summary>
    /// Returns <c>true</c> to keep the connection loop running, <c>false</c>
    /// to terminate the session and close the socket.
    /// </summary>
    private async Task<bool> HandleFrameAsync(
        Stream stream, DecodedFrame frame, string remote, CancellationToken ct)
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
                EnsureOrderAdapterWired();
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
                        await _orders!.HandleNewOrderSingleAsync(stream, frame.Payload, _scope!, ct)
                            .ConfigureAwait(false);
                        TouchOutbound();
                    }
                    finally { _writeMutex.Release(); }
                }
                return true;

            case OrderCancelRequestData.MESSAGE_ID:
                if (_sm.State != FixpSessionState.Established) goto default;
                EnsureOrderAdapterWired();
                {
                    var fresh = await TrackInboundAppMessageAsync(
                        stream, frame.Payload, OrderCancelRequestData.BLOCK_LENGTH, ct).ConfigureAwait(false);
                    if (!fresh) return true;

                    await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _orders!.HandleOrderCancelRequestAsync(stream, frame.Payload, _scope!, ct)
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
        Stream stream, DecodedFrame frame, string remote, CancellationToken ct)
    {
        if (frame.Payload.Length < NegotiateData.BLOCK_LENGTH)
        {
            _logger.LogInformation(
                "fixp.negotiate.reject reason=INVALID_FRAME remote={Remote}", remote);
            FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:invalid_frame"));
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
            FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:fsm"));
            await SendActionAsync(stream, fsmAction, sessionId, sessionVerId, ct).ConfigureAwait(false);
            return !fsmAction.IsTerminating;
        }

        // Rate limit: per-IP check (pre-auth)
        if (_rateLimiter is not null)
        {
            var remoteIp = GetRemoteIp();
            if (remoteIp is not null && !_rateLimiter.TryAcquireForIp(remoteIp, _clock))
            {
                _logger.LogInformation(
                    "fixp.negotiate.reject reason=RATE_LIMIT_IP remote={Remote}", remote);
                FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:rate_limit_ip"));
                await WriteNegotiateRejectAsync(stream, sessionId, sessionVerId,
                    NegotiationRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
                _sm.ForceTerminated();
                return false;
            }
        }

        // Auth: pull the Credentials var-data field from the SBE payload
        // and resolve it through the credential registry.
        if (!TryReadCredentials(frame.Payload, out var token))
        {
            _logger.LogInformation(
                "fixp.negotiate.reject reason=CREDENTIALS detail=malformed-buffer remote={Remote}",
                remote);
            FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:credentials"));
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
            FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:credentials"));
            await WriteNegotiateRejectAsync(stream, sessionId, sessionVerId,
                NegotiationRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        // mTLS cert↔credential binding (RFC user-bot-fixp-mtls-v0 §4.3). When a
        // credential is pinned, the client cert presented during the TLS
        // handshake (stashed on the connection) must match by SHA-256
        // thumbprint. Under Optional mode a certless connection is admissible,
        // so the pin is enforced only when a cert was actually presented; under
        // Required mode a cert is always present so the pin always applies.
        if (credential.BoundCertThumbprint is { } pinnedThumbprint &&
            _clientCertificate is not null &&
            !ThumbprintMatches(_clientCertificate, pinnedThumbprint))
        {
            _logger.LogWarning(
                "fixp.mtls.binding_mismatch credShortId={CredShortId} userId={UserId} remote={Remote}",
                credential.CredShortId, credential.UserId, remote);
            FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:binding_mismatch"));
            await WriteNegotiateRejectAsync(stream, sessionId, sessionVerId,
                NegotiationRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        // Rate limit: per-credential check (post-auth)
        if (_rateLimiter is not null && !_rateLimiter.TryAcquireForCredential(credential.Id, _clock))
        {
            _logger.LogInformation(
                "fixp.negotiate.reject reason=RATE_LIMIT_CREDENTIAL credShortId={CredShortId} remote={Remote}",
                credential.CredShortId, remote);
            FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:rate_limit_credential"));
            await WriteNegotiateRejectAsync(stream, sessionId, sessionVerId,
                NegotiationRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }

        // Per-user max sessions check
        if (_sessionCounter is not null &&
            !_sessionCounter.TryIncrement(credential.UserId, _options.MaxSessionsPerUser))
        {
            _logger.LogInformation(
                "fixp.negotiate.reject reason=MAX_SESSIONS_PER_USER userId={UserId} remote={Remote}",
                credential.UserId, remote);
            FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject:max_sessions"));
            await WriteNegotiateRejectAsync(stream, sessionId, sessionVerId,
                NegotiationRejectCode.CREDENTIALS, ct).ConfigureAwait(false);
            _sm.ForceTerminated();
            return false;
        }
        if (_sessionCounter is not null)
            _userSlotHeld = true;

        // Allocate (or load) the per-credential session state up-front so
        // Establish can validate sid/ver synchronously off the resolved
        // scope without re-querying the registry.
        var sessionState = await _sessions.GetOrCreateAsync(credential.Id, ct).ConfigureAwait(false);
        var principal = new BotSessionPrincipal(
            credential.UserId, credential.Id, credential.CredShortId, credential.Label,
            credential.FirmId);
        _scope = new FixpConnectionScope(_connectionId, principal, sessionState);

        _logger.LogInformation(
            "fixp.negotiate.ok credShortId={CredShortId} userId={UserId} remote={Remote} connectionId={ConnectionId}",
            credential.CredShortId, credential.UserId, remote, _connectionId);

        FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "ok"));
        await WriteNegotiateResponseAsync(stream, sessionId, sessionVerId, ct).ConfigureAwait(false);
        return true;
    }

    // ─── Establish ───────────────────────────────────────────────────────

    private async Task<bool> HandleEstablishAsync(
        Stream stream, DecodedFrame frame, CancellationToken ct)
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
        FixpListenerMetrics.SessionsActive.Add(1);
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
            // RFC §5.3 / P8 / F3 — start the per-connection bounded
            // outbound channel + dedicated drain loop BEFORE we publish
            // ourselves to the directory, so the very first
            // multiplexer push lands on a live writer (no race where
            // TryGet returns us but the writer is not yet wired).
            _outboundWriter = new FixpOutboundChannelWriter(
                capacity: Math.Max(1, _options.Buffers.OutboundChannelCapacity),
                writeAsync: WriteOutboundFromDrainLoopAsync,
                connectionId: _connectionId,
                logger: _logger);
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
        Stream stream, DecodedFrame frame, CancellationToken ct)
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
        // RFC §5.3 / P8: post-Establish writes must serialise against
        // the per-connection drain loop's outbound writes. The pre-P8
        // path was racy too (drain loop's Task.Run used the mutex but
        // this call site never did); now that the writer reliably
        // takes _writeMutex on every drained frame, holding it here
        // closes the byte-interleave window for free.
        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendActionAsync(stream, action, sid, ver, ct).ConfigureAwait(false);
        }
        finally
        {
            try { _writeMutex.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }
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
    private Task<bool> TrackInboundAppMessageAsync(
        Stream stream, byte[] payload, int blockLength, CancellationToken ct)
    {
        // The InboundBusinessHeader is the first field of the message
        // block (offset 0 within the SBE block — see InboundBusinessHeader
        // and NewOrderSingleData layout). Defensive bounds check first.
        if (payload.Length < blockLength) return Task.FromResult(true);
        var header = MemoryMarshal.Read<InboundBusinessHeader>(payload);
        return TrackInboundAppSeqAsync(stream, (uint)header.MsgSeqNum, ct);
    }

    /// <summary>
    /// RFC §5.6 (P10/F6) zero-copy entry point for the
    /// <c>NewOrderSingle</c>/<c>OrderCancelRequest</c> sequence-watermark
    /// bookkeeping. Identical semantics to
    /// <see cref="TrackInboundAppMessageAsync"/> but takes the already
    /// decoded <c>MsgSeqNum</c> field instead of re-decoding the
    /// inbound business header from a heap-copied payload.
    /// </summary>
    private async Task<bool> TrackInboundAppSeqAsync(
        Stream stream, uint msgSeqNum, CancellationToken ct)
    {
        var seq = (ulong)msgSeqNum;
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
        Stream stream, DecodedFrame frame, CancellationToken ct)
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

    /// <summary>
    /// RFC §5.6 (P10/F6) zero-copy variant of
    /// <see cref="HandleInboundSequenceAsync"/>. Behaviour and watermark
    /// arithmetic are unchanged; the only difference is that
    /// <see cref="DecodedSequence.NextSeqNo"/> is supplied by the
    /// dispatcher's synchronous SBE decode instead of being re-read
    /// from a heap-copied payload.
    /// </summary>
    private async Task<bool> HandleInboundSequenceZeroCopyKeepAliveAsync(
        Stream stream, DecodedSequence decoded, CancellationToken ct)
    {
        await HandleInboundSequenceZeroCopyAsync(stream, decoded, ct).ConfigureAwait(false);
        return true;
    }

    private async Task HandleInboundSequenceZeroCopyAsync(
        Stream stream, DecodedSequence decoded, CancellationToken ct)
    {
        var nextSeqNo = decoded.NextSeqNo;
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
        else if (nextSeqNo < expected)
        {
            _logger.LogWarning(
                "fixp.inbound.sequence.behind connectionId={ConnectionId} expected={Expected} botNext={BotNext}",
                _connectionId, expected, nextSeqNo);
        }
    }

    /// <summary>
    /// RFC §5.6 (P10/F6). Zero-copy <c>NewOrderSingle</c> handler. The
    /// caller (<see cref="DispatchFrameAsync"/>) has already decoded
    /// the SBE block synchronously into <paramref name="decoded"/>, so
    /// no <c>byte[]</c> survives across this method's awaits. The
    /// behaviour mirrors the legacy switch arm: track inbound seq, take
    /// the shared write mutex, dispatch to the order adapter, then
    /// touch the outbound timestamp.
    /// </summary>
    private async Task<bool> HandleNewOrderSingleZeroCopyAsync(
        Stream stream, DecodedNewOrderSingle decoded, CancellationToken ct)
    {
        var fresh = await TrackInboundAppSeqAsync(stream, decoded.MsgSeqNum, ct)
            .ConfigureAwait(false);
        if (!fresh) return true;

        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _orders!.HandleNewOrderSingleAsync(stream, decoded, _scope!, ct)
                .ConfigureAwait(false);
            TouchOutbound();
        }
        finally { _writeMutex.Release(); }
        return true;
    }

    /// <summary>
    /// RFC §5.6 (P10/F6). Zero-copy <c>OrderCancelRequest</c> handler.
    /// Same structural contract as
    /// <see cref="HandleNewOrderSingleZeroCopyAsync"/>.
    /// </summary>
    private async Task<bool> HandleOrderCancelRequestZeroCopyAsync(
        Stream stream, DecodedOrderCancelRequest decoded, CancellationToken ct)
    {
        var fresh = await TrackInboundAppSeqAsync(stream, decoded.MsgSeqNum, ct)
            .ConfigureAwait(false);
        if (!fresh) return true;

        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _orders!.HandleOrderCancelRequestAsync(stream, decoded, _scope!, ct)
                .ConfigureAwait(false);
            TouchOutbound();
        }
        finally { _writeMutex.Release(); }
        return true;
    }

    private async Task HandleInboundRetransmitRequestAsync(
        Stream stream, DecodedFrame frame, CancellationToken ct)
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
            FixpListenerMetrics.RetransmitRequestsTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject"));
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
            FixpListenerMetrics.RetransmitRequestsTotal.Add(1, new KeyValuePair<string, object?>("outcome", "reject"));
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
            FixpListenerMetrics.RetransmitRequestsTotal.Add(1, new KeyValuePair<string, object?>("outcome", "replay"));
        }
        finally
        {
            _writeMutex.Release();
        }
    }

    private async Task SendNotAppliedAsync(
        Stream stream, ulong fromSeqNo, uint count, CancellationToken ct)
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
        Stream stream, uint sessionId, ulong requestTimestamp,
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
    private void StartHeartbeatLoop(Stream stream)
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

    private async Task SendHeartbeatSequenceAsync(Stream stream, CancellationToken ct)
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
        Stream stream,
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
        Stream stream, uint sessionId, ulong sessionVerId, CancellationToken ct)
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
        Stream stream, uint sessionId, ulong sessionVerId,
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
        Stream stream, uint sessionId, ulong sessionVerId, CancellationToken ct)
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
        Stream stream, uint sessionId, ulong sessionVerId,
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
        Stream stream,
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
        Stream stream, TerminationCode code, CancellationToken ct)
    {
        try { await WriteTerminateAsync(stream, 0, 0, code, ct).ConfigureAwait(false); }
        catch { /* best effort */ }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Constant-time check that <paramref name="certificate"/>'s SHA-256
    /// thumbprint equals the credential's pinned <paramref name="expectedHex"/>
    /// (RFC §4.3). <paramref name="expectedHex"/> is canonical upper-case
    /// 64-hex (validated at the registry); a malformed pin can never match.
    /// The comparison is done over the raw 32-byte hashes via
    /// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// so it does not leak via timing.
    /// </summary>
    private static bool ThumbprintMatches(
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        string expectedHex)
    {
        if (expectedHex.Length != 64)
            return false;

        Span<byte> expected = stackalloc byte[32];
        if (!TryParseHex(expectedHex, expected))
            return false;

        var actual = certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        if (hex.Length != destination.Length * 2)
            return false;

        for (var i = 0; i < destination.Length; i++)
        {
            var hi = FromHexNibble(hex[i * 2]);
            var lo = FromHexNibble(hex[(i * 2) + 1]);
            if (hi < 0 || lo < 0)
                return false;
            destination[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int FromHexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

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

    private IPAddress? GetRemoteIp()
    {
        try
        {
            return _tcpClient.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address : null;
        }
        catch { return null; }
    }

    // ─── IBotSessionOutboundSender (sub-issue F #172, RFC §5.3 / P8 / F3) ─

    /// <summary>
    /// Synchronously hands <paramref name="frame"/> to the
    /// per-connection bounded outbound channel (RFC §5.3 / P8 / F3).
    /// Non-blocking: returns <c>false</c> when the connection has
    /// closed or the channel is full (slow-consumer backpressure,
    /// §5.3.1).
    ///
    /// <para>Replaces the pre-F3 <c>Task.Run</c>-per-send fire-and-
    /// forget path: a single dedicated drain loop now owns the socket
    /// and writes serially under <see cref="_writeMutex"/> to interleave
    /// safely with handshake/order-ack writes from the request loop.
    /// One <see cref="Task"/> per connection, not per outbound message.</para>
    ///
    /// <para>The drain loop NEVER disposes <paramref name="frame"/> —
    /// the per-credential <see cref="BotOutboundBuffer"/> is the sole
    /// disposer (RFC §5.5 single-disposer rule). Lifetime safety
    /// across the awaited socket write is guaranteed by the protocol:
    /// a bot can only ack a watermark for sequences it has actually
    /// received, so an unsent frame's seq cannot be evicted under us;
    /// overflow / version-bump force-closes the connection (and ends
    /// the drain loop) BEFORE the buffer's <c>Reset</c> clears pooled
    /// owners. See <see cref="FixpOutboundChannelWriter"/> doc.</para>
    /// </summary>
    bool IBotSessionOutboundSender.TryEnqueue(OutboundFrame frame)
    {
        if (_closed) return false;
        var writer = _outboundWriter;
        if (writer is null) return false;
        return writer.TryEnqueue(frame);
    }

    /// <summary>
    /// Drain-loop callback: serialises writes against handshake /
    /// order-ack writes via <see cref="_writeMutex"/>. Returns
    /// <c>false</c> when the writer should stop draining (stream
    /// gone or socket failure). Errors during the actual socket
    /// write close the stream and return <c>false</c>; the read loop
    /// observes the broken stream and runs the deregister/release
    /// path.
    ///
    /// <para>Intentionally does NOT short-circuit on
    /// <see cref="_closed"/>: shutdown drain (RFC §5.3.2) runs while
    /// <c>_closed</c> may already be set, and we must still flush
    /// queued frames before the stream is torn down. The stream's
    /// own state (closed / faulted) is the authoritative signal —
    /// surfaced as an exception from
    /// <see cref="System.IO.Stream.WriteAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>.</para>
    /// </summary>
    private async ValueTask<bool> WriteOutboundFromDrainLoopAsync(
        ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return false;

        await _writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            TouchOutbound();
            return true;
        }
        catch (OperationCanceledException)
        {
            // Shutdown / write-mutex wait was cancelled. Surface to
            // the drain loop, which treats cancellation as "shutdown
            // drain timeout fired" and returns without disposing.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "fixp.outbound.write.error connectionId={ConnectionId}", _connectionId);
            _closed = true;
            try { stream.Close(); } catch { /* ignore */ }
            return false;
        }
        finally
        {
            // Defensive: WaitAsync may have thrown ObjectDisposedException
            // if Dispose() raced with us. Guard the release.
            try { _writeMutex.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }
    }

    public void Dispose()
    {
        // Used by the multiplexer's overflow path to force-close.
        _closed = true;
        StopHeartbeatLoop();

        // RFC §5.3.2 shutdown drain: best-effort flush of in-flight
        // outbound frames, bounded by OutboundDrainShutdownTimeout.
        // Frames not drained remain owned by the per-credential
        // BotOutboundBuffer and ride retransmit on next reconnect —
        // never silently dropped. Drain loop never disposes (RFC §5.5).
        var writer = _outboundWriter;
        if (writer is not null)
        {
            try
            {
                writer.CompleteAsync(_options.Buffers.OutboundDrainShutdownTimeout)
                    .GetAwaiter().GetResult();
            }
            catch { /* best-effort on shutdown */ }
        }

        try { _stream?.Close(); } catch { /* ignore */ }
        try { _tcpClient.Close(); } catch { /* ignore */ }
        _writeMutex.Dispose();
    }
}
