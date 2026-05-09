using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Handshake;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Manages the FIXP session lifecycle for a single accepted TCP connection.
/// Reads SOFH-framed SBE messages, drives <see cref="FixpHandshakeStateMachine"/>,
/// and writes responses. Auth is stubbed (always-accept) in sub-issue B.
/// </summary>
internal sealed class FixpSessionConnection
{
    // SBE MessageHeader fields written into every outbound frame.
    private const ushort SchemaIdV6 = 1;
    private const ushort VersionNegotiateResponse = 6;
    private const ushort VersionEstablishAck = 6;
    private const ushort VersionTerminate = 0;

    private readonly TcpClient _tcpClient;
    private readonly ILogger _logger;
    private readonly FixpHandshakeStateMachine _sm = new();

    public FixpSessionConnection(TcpClient tcpClient, ILogger logger)
    {
        _tcpClient = tcpClient;
        _logger = logger;
    }

    /// <summary>
    /// Runs the connection loop until the peer closes the connection,
    /// the handshake terminates, or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var client = _tcpClient;
        var stream = client.GetStream();
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

                if (n == 0) break; // peer closed connection

                reader.Append(readBuf.AsSpan(0, n));

                if (reader.HasProtocolError)
                {
                    _logger.LogWarning("FIXP framing error: {Msg}", reader.ProtocolErrorMessage);
                    await BestEffortSendTerminateAsync(stream, TerminationCode.INVALID_SOFH, ct)
                        .ConfigureAwait(false);
                    break;
                }

                while (reader.TryReadFrame(out var frame))
                {
                    // Decode synchronously (frame.Payload span must not cross an await).
                    var decoded = DecodeFrame(frame);
                    var action = Dispatch(decoded);

                    await SendResponseAsync(stream, action, decoded.SessionId, decoded.SessionVerId, ct)
                        .ConfigureAwait(false);

                    if (action.IsTerminating) return;
                }

                if (reader.HasProtocolError)
                {
                    await BestEffortSendTerminateAsync(stream, TerminationCode.INVALID_SOFH, ct)
                        .ConfigureAwait(false);
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
        }
    }

    // ─── Synchronous helpers (no await — spans are safe here) ────────────────

    private struct DecodedFrame
    {
        public ushort TemplateId;
        public bool DecodeFailed;   // true when a known template has an undersized payload
        public uint SessionId;      // (uint)SessionID
        public ulong SessionVerId;  // (ulong)SessionVerID
        public TerminationCode TermCode;
    }

    private static DecodedFrame DecodeFrame(SofhFrame frame)
    {
        var d = new DecodedFrame { TemplateId = frame.TemplateId };

        switch (frame.TemplateId)
        {
            case NegotiateData.MESSAGE_ID when frame.Payload.Length >= NegotiateData.BLOCK_LENGTH:
                {
                    var msg = MemoryMarshal.Read<NegotiateData>(frame.Payload);
                    d.SessionId = (uint)msg.SessionID;
                    d.SessionVerId = (ulong)msg.SessionVerID;
                    break;
                }
            case EstablishData.MESSAGE_ID when frame.Payload.Length >= EstablishData.BLOCK_LENGTH:
                {
                    var msg = MemoryMarshal.Read<EstablishData>(frame.Payload);
                    d.SessionId = (uint)msg.SessionID;
                    d.SessionVerId = (ulong)msg.SessionVerID;
                    break;
                }
            case TerminateData.MESSAGE_ID when frame.Payload.Length >= TerminateData.BLOCK_LENGTH:
                {
                    var msg = MemoryMarshal.Read<TerminateData>(frame.Payload);
                    d.SessionId = (uint)msg.SessionID;
                    d.SessionVerId = (ulong)msg.SessionVerID;
                    d.TermCode = msg.TerminationCode;
                    break;
                }
            // Known handshake template with undersized payload — reject rather than dispatch zeroes.
            case NegotiateData.MESSAGE_ID:
            case EstablishData.MESSAGE_ID:
            case TerminateData.MESSAGE_ID:
                d.DecodeFailed = true;
                break;
        }

        return d;
    }

    private HandshakeAction Dispatch(in DecodedFrame d)
    {
        if (d.DecodeFailed)
            return HandshakeAction.Terminate(TerminationCode.UNSPECIFIED);

        switch (d.TemplateId)
        {
            case NegotiateData.MESSAGE_ID:
                {
                    var msg = new NegotiateData
                    {
                        SessionID = (SessionID)d.SessionId,
                        SessionVerID = (SessionVerID)d.SessionVerId,
                    };
                    return _sm.OnNegotiate(in msg);
                }
            case EstablishData.MESSAGE_ID:
                {
                    var msg = new EstablishData
                    {
                        SessionID = (SessionID)d.SessionId,
                        SessionVerID = (SessionVerID)d.SessionVerId,
                    };
                    return _sm.OnEstablish(in msg);
                }
            case TerminateData.MESSAGE_ID:
                {
                    var msg = new TerminateData
                    {
                        SessionID = (SessionID)d.SessionId,
                        SessionVerID = (SessionVerID)d.SessionVerId,
                        TerminationCode = d.TermCode,
                    };
                    return _sm.OnTerminate(in msg);
                }
            default:
                return _sm.State == FixpSessionState.Established
                    ? HandshakeAction.NoOp
                    : _sm.OnApplicationMessageBeforeEstablished();
        }
    }

    // ─── Async response writers ───────────────────────────────────────────────

    private async Task SendResponseAsync(
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
            BuildNegotiateResponseFrame(buf.AsSpan(0, frameSize), sessionId, sessionVerId);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void BuildNegotiateResponseFrame(Span<byte> dest, uint sessionId, ulong sessionVerId)
    {
        Span<byte> body = stackalloc byte[NegotiateResponseData.BLOCK_LENGTH];
        body.Clear();
        ref var resp = ref MemoryMarshal.AsRef<NegotiateResponseData>(body);
        resp.SessionID = (SessionID)sessionId;
        resp.SessionVerID = (SessionVerID)sessionVerId;
        SofhFrameWriter.WriteFrame(dest,
            (ushort)NegotiateResponseData.BLOCK_LENGTH,
            (ushort)NegotiateResponseData.MESSAGE_ID,
            SchemaIdV6, VersionNegotiateResponse,
            body);
    }

    private static async Task WriteEstablishAckAsync(
        NetworkStream stream, uint sessionId, ulong sessionVerId, CancellationToken ct)
    {
        var frameSize = SofhFrameWriter.FrameSize(EstablishAckData.BLOCK_LENGTH);
        var buf = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            BuildEstablishAckFrame(buf.AsSpan(0, frameSize), sessionId, sessionVerId);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void BuildEstablishAckFrame(Span<byte> dest, uint sessionId, ulong sessionVerId)
    {
        Span<byte> body = stackalloc byte[EstablishAckData.BLOCK_LENGTH];
        body.Clear();
        ref var resp = ref MemoryMarshal.AsRef<EstablishAckData>(body);
        resp.SessionID = (SessionID)sessionId;
        resp.SessionVerID = (SessionVerID)sessionVerId;
        SofhFrameWriter.WriteFrame(dest,
            (ushort)EstablishAckData.BLOCK_LENGTH,
            (ushort)EstablishAckData.MESSAGE_ID,
            SchemaIdV6, VersionEstablishAck,
            body);
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
            BuildTerminateFrame(buf.AsSpan(0, frameSize), sessionId, sessionVerId, code);
            await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void BuildTerminateFrame(
        Span<byte> dest, uint sessionId, ulong sessionVerId, TerminationCode code)
    {
        Span<byte> body = stackalloc byte[TerminateData.BLOCK_LENGTH];
        body.Clear();
        ref var msg = ref MemoryMarshal.AsRef<TerminateData>(body);
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        msg.TerminationCode = code;
        SofhFrameWriter.WriteFrame(dest,
            (ushort)TerminateData.BLOCK_LENGTH,
            (ushort)TerminateData.MESSAGE_ID,
            SchemaIdV6, VersionTerminate,
            body);
    }

    private static async Task BestEffortSendTerminateAsync(
        NetworkStream stream, TerminationCode code, CancellationToken ct)
    {
        try
        {
            var frameSize = SofhFrameWriter.FrameSize(TerminateData.BLOCK_LENGTH);
            var buf = ArrayPool<byte>.Shared.Rent(frameSize);
            try
            {
                BuildTerminateFrame(buf.AsSpan(0, frameSize), 0, 0, code);
                await stream.WriteAsync(buf, 0, frameSize, ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        catch { /* best effort */ }
    }
}
