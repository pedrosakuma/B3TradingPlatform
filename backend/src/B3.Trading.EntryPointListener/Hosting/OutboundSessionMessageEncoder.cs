using System.Buffers;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.EntryPointListener.Framing;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #173 (G). Pre-frames the FIXP session-control messages the
/// listener sends after Establish (heartbeat <c>Sequence</c>, gap-signal
/// <c>NotApplied</c>, retransmit responses). Mirrors the per-write
/// <c>ArrayPool</c> shape used by <see cref="OutboundExecutionReportEncoder"/>
/// so all encoded bytes are heap arrays sized exactly to the frame —
/// the caller decides whether to ship them via the multiplexer's
/// <see cref="IBotSessionOutboundSender"/> path or the connection's
/// owning write mutex.
/// </summary>
internal static class OutboundSessionMessageEncoder
{
    private const ushort SchemaIdV6 = 1;

    // FIXP session-control messages are schema-version 6 in V6 except
    // Terminate which is left at version 0 to match B/D/E/F's existing
    // wire fingerprints — see FixpSessionConnection.WriteTerminateAsync.
    private const ushort VersionSequence = 6;
    private const ushort VersionNotApplied = 6;
    private const ushort VersionRetransmission = 6;
    private const ushort VersionRetransmitReject = 6;

    /// <summary>
    /// Encodes a SOFH-framed <c>Sequence(NextSeqNo)</c>. Used for
    /// per-connection heartbeat (RFC §4.7) and as the framing message
    /// every <c>Retransmission</c> response is preceded by per FIXP
    /// (NextSeqNo = the seq of the first replayed message).
    /// </summary>
    public static byte[] EncodeSequence(ulong nextSeqNo)
    {
        var frameSize = SofhFrameWriter.FrameSize(SequenceData.BLOCK_LENGTH);
        var buf = new byte[frameSize];
        Span<byte> body = stackalloc byte[SequenceData.BLOCK_LENGTH];
        body.Clear();
        ref var msg = ref MemoryMarshal.AsRef<SequenceData>(body);
        msg.NextSeqNo = (SeqNum)nextSeqNo;
        SofhFrameWriter.WriteFrame(buf,
            (ushort)SequenceData.BLOCK_LENGTH,
            (ushort)SequenceData.MESSAGE_ID,
            SchemaIdV6, VersionSequence,
            body);
        return buf;
    }

    /// <summary>
    /// Encodes a SOFH-framed <c>NotApplied(FromSeqNo, Count)</c>. The
    /// listener emits this when it observes an inbound seq gap from the
    /// bot (RFC §4.7). NotApplied is informational on B3's idempotent
    /// trade-entry flow — the bot is expected to reconcile via REST
    /// rather than retransmit gaped seqs (new-order retries would
    /// duplicate ClOrdIds).
    /// </summary>
    public static byte[] EncodeNotApplied(ulong fromSeqNo, uint count)
    {
        var frameSize = SofhFrameWriter.FrameSize(NotAppliedData.BLOCK_LENGTH);
        var buf = new byte[frameSize];
        Span<byte> body = stackalloc byte[NotAppliedData.BLOCK_LENGTH];
        body.Clear();
        ref var msg = ref MemoryMarshal.AsRef<NotAppliedData>(body);
        msg.FromSeqNo = (SeqNum)fromSeqNo;
        msg.Count = (MessageCounter)count;
        SofhFrameWriter.WriteFrame(buf,
            (ushort)NotAppliedData.BLOCK_LENGTH,
            (ushort)NotAppliedData.MESSAGE_ID,
            SchemaIdV6, VersionNotApplied,
            body);
        return buf;
    }

    /// <summary>
    /// Encodes a SOFH-framed <c>Retransmission(SessionID, RequestTimestamp,
    /// NextSeqNo, Count)</c>. Per FIXP V6 a Retransmission is a framing
    /// message that precedes the actual replayed application messages on
    /// the wire — the bot's parser uses it to know how many of the
    /// frames immediately following are part of the replay window.
    /// </summary>
    public static byte[] EncodeRetransmission(
        uint sessionId, ulong requestTimestampNanos, ulong nextSeqNo, uint count)
    {
        var frameSize = SofhFrameWriter.FrameSize(RetransmissionData.BLOCK_LENGTH);
        var buf = new byte[frameSize];
        Span<byte> body = stackalloc byte[RetransmissionData.BLOCK_LENGTH];
        body.Clear();
        ref var msg = ref MemoryMarshal.AsRef<RetransmissionData>(body);
        msg.SessionID = (SessionID)sessionId;
        msg.RequestTimestamp = new UTCTimestampNanos { Time = requestTimestampNanos };
        msg.NextSeqNo = (SeqNum)nextSeqNo;
        msg.Count = (MessageCounter)count;
        SofhFrameWriter.WriteFrame(buf,
            (ushort)RetransmissionData.BLOCK_LENGTH,
            (ushort)RetransmissionData.MESSAGE_ID,
            SchemaIdV6, VersionRetransmission,
            body);
        return buf;
    }

    /// <summary>
    /// Encodes a SOFH-framed <c>RetransmitReject(SessionID,
    /// RequestTimestamp, RetransmitRejectCode)</c>. The reject code is
    /// the SBE enum value as-is — see <see cref="RetransmitRejectCode"/>
    /// for the full set the schema accepts.
    /// </summary>
    public static byte[] EncodeRetransmitReject(
        uint sessionId, ulong requestTimestampNanos, RetransmitRejectCode code)
    {
        var frameSize = SofhFrameWriter.FrameSize(RetransmitRejectData.BLOCK_LENGTH);
        var buf = new byte[frameSize];
        Span<byte> body = stackalloc byte[RetransmitRejectData.BLOCK_LENGTH];
        body.Clear();
        ref var msg = ref MemoryMarshal.AsRef<RetransmitRejectData>(body);
        msg.SessionID = (SessionID)sessionId;
        msg.RequestTimestamp = new UTCTimestampNanos { Time = requestTimestampNanos };
        msg.RetransmitRejectCode = code;
        SofhFrameWriter.WriteFrame(buf,
            (ushort)RetransmitRejectData.BLOCK_LENGTH,
            (ushort)RetransmitRejectData.MESSAGE_ID,
            SchemaIdV6, VersionRetransmitReject,
            body);
        return buf;
    }
}
