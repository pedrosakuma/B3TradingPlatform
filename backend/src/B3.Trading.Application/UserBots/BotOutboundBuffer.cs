namespace B3.Trading.Application.UserBots;

/// <summary>
/// Sub-issue #172 (F). Per-credential bounded buffer of outbound
/// application messages awaiting either send (when the bot is offline)
/// or potential retransmit-on-request (sub-issue G #173). Stores
/// <c>(seq, OutboundFrame)</c> pairs in arrival order, keyed by the
/// allocator-assigned outbound seq. Thread-safe.
///
/// <para><b>Single-disposer of pooled outbound memory (RFC §5.5,
/// issue #201).</b> When <see cref="Append"/> accepts an
/// <see cref="OutboundFrame"/> backed by an
/// <see cref="System.Buffers.IMemoryOwner{T}"/> rented from a
/// <see cref="System.Buffers.MemoryPool{T}"/>, this buffer becomes the
/// sole owner of that pooled memory. Disposal happens exactly once,
/// in one of these mutually-exclusive places:
/// <list type="bullet">
///   <item><see cref="EvictUpTo"/> — when the bot acks a watermark
///         past the frame's seq.</item>
///   <item><see cref="Append"/> overflow branch — bulk-clears every
///         buffered frame the moment the cap trips.</item>
///   <item><see cref="Append"/> rejected branch — disposes the
///         <i>incoming</i> frame before returning <c>false</c> so the
///         caller never has to.</item>
///   <item><see cref="Reset"/> — clears the buffer on the recovery
///         path after a version bump.</item>
/// </list>
/// No other call site disposes; the live-send / drain / retransmit
/// paths only borrow <see cref="OutboundFrame.Bytes"/>.</para>
///
/// <para>v0 is in-memory only — the RFC §4.8 explicitly defers WAL
/// persistence of the buffer; bots reconcile lost messages via REST
/// <c>/api/orders</c> on restart. The credential's
/// <c>LastCheckpointedOutboundSeq</c> watermark is the only thing
/// surviving restart, and a bot that asks G for a seq older than the
/// watermark gets a reject.</para>
///
/// <para>Overflow path: instead of silently dropping (which would let a
/// bot observe a gap without warning), <see cref="Append"/> invokes the
/// <see cref="OnOverflow"/> callback synchronously when the cap is hit.
/// The expected handler is the multiplexer's overflow path:
/// <c>BumpVersionAsync(reason="overflow")</c> + force-disconnect.
/// The callback runs while the buffer's internal lock is held — the
/// handler must NOT do async I/O inline; F's multiplexer fires the
/// async work onto a queue that drains outside the lock.</para>
/// </summary>
public sealed class BotOutboundBuffer
{
    /// <summary>
    /// Default cap — chosen to absorb several minutes of a high-rate
    /// bot's ER stream without triggering an overflow during a transient
    /// disconnect. Operators tune this via <c>BotErMultiplexerOptions</c>.
    /// </summary>
    public const int DefaultMaxMessages = 50_000;

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<ulong, LinkedListNode<Entry>> _index = new();
    private readonly int _maxMessages;
    private readonly Action<Guid>? _onOverflow;
    private readonly Guid _credentialId;
    private bool _overflowed;

    public BotOutboundBuffer(Guid credentialId, int maxMessages, Action<Guid>? onOverflow = null)
    {
        if (maxMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), "Cap must be positive.");
        _credentialId = credentialId;
        _maxMessages = maxMessages;
        _onOverflow = onOverflow;
    }

    /// <summary>Current count of buffered entries.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// True once <see cref="Append"/> has tripped the overflow callback.
    /// Stays true until <see cref="Reset"/> is called by the recovery
    /// path (after the bot reconnects with the bumped SessionVerId).
    /// </summary>
    public bool IsOverflowed
    {
        get { lock (_gate) return _overflowed; }
    }

    /// <summary>
    /// Appends an outbound <paramref name="frame"/> at sequence
    /// <paramref name="seq"/>. On success the buffer takes ownership of
    /// <paramref name="frame"/>'s pooled memory (if any) — see the
    /// single-disposer rule on the type-level doc. Returns <c>false</c>
    /// when the buffer is closed or the cap is hit; in both refused
    /// branches <see cref="OutboundFrame.DisposeOwner"/> is invoked on
    /// the rejected frame before returning, so the caller must NOT
    /// dispose. On overflow the buffer is bulk-cleared (every entry's
    /// pooled owner disposed) and <see cref="OnOverflow"/> fires.
    /// Subsequent <c>Append</c> calls return <c>false</c> (and dispose
    /// the rejected frame) until <see cref="Reset"/> is called.
    /// </summary>
    public bool Append(ulong seq, OutboundFrame frame)
    {
        lock (_gate)
        {
            if (_overflowed)
            {
                // Single-disposer rule: nothing else may dispose the
                // rejected frame, so the buffer does it on the way out.
                frame.DisposeOwner();
                return false;
            }
            if (_entries.Count >= _maxMessages)
            {
                _overflowed = true;
                DisposeAllLocked();
                _entries.Clear();
                _index.Clear();
                _onOverflow?.Invoke(_credentialId);
                frame.DisposeOwner();
                return false;
            }

            // Take ownership — no defensive copy. The encoder rented
            // the pooled buffer specifically for us; copying would be
            // the second copy that issue #201 (RFC §5.5 / F5) exists
            // to eliminate. The buffer holds the frame until the bot's
            // acked-watermark eviction (or overflow / reset) releases
            // the pooled owner.
            var node = _entries.AddLast(new Entry(seq, frame));
            _index[seq] = node;
            return true;
        }
    }

    /// <summary>
    /// Convenience overload for callers (and tests) whose payload is a
    /// plain in-memory buffer rather than a pooled frame. Wraps the
    /// bytes in <see cref="OutboundFrame.Unowned"/> — there is nothing
    /// to dispose, but the contract is otherwise identical to the
    /// frame-taking overload (see the single-disposer rule).
    /// </summary>
    public bool Append(ulong seq, ReadOnlyMemory<byte> bytes)
        => Append(seq, OutboundFrame.Unowned(bytes));

    /// <summary>
    /// Returns the buffered messages in <c>[fromSeq, toSeq]</c>, sorted
    /// by seq ascending. Returns an empty list when no entries match.
    /// Sub-issue G consumes this for <c>RetransmitRequest</c> handling.
    ///
    /// <para>Each returned <see cref="BufferedOutboundMessage.Bytes"/>
    /// is a private heap snapshot taken under <c>_gate</c> — the
    /// retransmit replay loop awaits across socket writes, and a later
    /// <see cref="EvictUpTo"/> / overflow / <see cref="Reset"/> would
    /// otherwise dispose the underlying pooled owner mid-write. The
    /// snapshot keeps the buffer the sole disposer of pooled memory
    /// (RFC §5.5) without exposing that memory across an
    /// <c>await</c>. Retransmit is a rare recovery path; the copy
    /// cost is on the cold path by design.</para>
    /// </summary>
    public IReadOnlyList<BufferedOutboundMessage> GetRange(ulong fromSeq, ulong toSeq)
    {
        if (toSeq < fromSeq) return Array.Empty<BufferedOutboundMessage>();
        lock (_gate)
        {
            var list = new List<BufferedOutboundMessage>();
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Seq < fromSeq) continue;
                if (node.Value.Seq > toSeq) break;
                // Snapshot under the lock; see the lifetime note above.
                list.Add(new BufferedOutboundMessage(node.Value.Seq, node.Value.Frame.Bytes.ToArray()));
            }
            return list;
        }
    }

    /// <summary>
    /// Drops every entry with <c>seq ≤ throughSeq</c> and disposes the
    /// pooled <c>Owner</c> backing each dropped frame (single-disposer
    /// rule, RFC §5.5). Cleanup hook for when the bot acknowledges its
    /// inbound watermark via the next <c>Sequence</c> message it sends
    /// (G's responsibility to call). Idempotent.
    /// </summary>
    public void EvictUpTo(ulong throughSeq)
    {
        lock (_gate)
        {
            while (_entries.First is { } first && first.Value.Seq <= throughSeq)
            {
                _index.Remove(first.Value.Seq);
                first.Value.Frame.DisposeOwner();
                _entries.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// Clears the buffer and resets the overflow flag. Disposes every
    /// pooled <c>Owner</c> still held (single-disposer rule, RFC §5.5).
    /// Called by the recovery path after the credential's
    /// <c>SessionVerId</c> has been bumped and the offending connection
    /// forcibly closed — the next reconnect attempt fails Establish
    /// with the new ver, the bot reconciles via REST, and we start
    /// fresh.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            DisposeAllLocked();
            _entries.Clear();
            _index.Clear();
            _overflowed = false;
        }
    }

    private void DisposeAllLocked()
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            node.Value.Frame.DisposeOwner();
        }
    }

    private readonly record struct Entry(ulong Seq, OutboundFrame Frame);
}

/// <summary>
/// Outbound message returned by <see cref="BotOutboundBuffer.GetRange"/>.
/// </summary>
public readonly record struct BufferedOutboundMessage(ulong Seq, ReadOnlyMemory<byte> Bytes);
