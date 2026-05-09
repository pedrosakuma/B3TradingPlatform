namespace B3.Trading.Application.UserBots;

/// <summary>
/// Sub-issue #172 (F). Per-credential bounded buffer of outbound
/// application messages awaiting either send (when the bot is offline)
/// or potential retransmit-on-request (sub-issue G #173). Stores
/// <c>(seq, rawSbeBytes)</c> pairs in arrival order, keyed by the
/// allocator-assigned outbound seq. Thread-safe.
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
    /// Appends an outbound message. Returns <c>false</c> when the cap is
    /// hit — the buffer is cleared and <see cref="OnOverflow"/> fires.
    /// Subsequent <c>Append</c> calls return <c>false</c> and do nothing
    /// until <see cref="Reset"/> is called.
    /// </summary>
    public bool Append(ulong seq, ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            if (_overflowed) return false;
            if (_entries.Count >= _maxMessages)
            {
                _overflowed = true;
                _entries.Clear();
                _index.Clear();
                _onOverflow?.Invoke(_credentialId);
                return false;
            }

            // Defensive copy — the caller's `bytes` may come from a pooled
            // buffer that gets returned to the pool after the publish call
            // returns. Holding a reference to a reused span would corrupt
            // both the buffer and the next caller.
            var copy = bytes.ToArray();
            var node = _entries.AddLast(new Entry(seq, copy));
            _index[seq] = node;
            return true;
        }
    }

    /// <summary>
    /// Returns the buffered messages in <c>[fromSeq, toSeq]</c>, sorted
    /// by seq ascending. Returns an empty list when no entries match.
    /// Sub-issue G consumes this for <c>RetransmitRequest</c> handling.
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
                list.Add(new BufferedOutboundMessage(node.Value.Seq, node.Value.Bytes));
            }
            return list;
        }
    }

    /// <summary>
    /// Drops every entry with <c>seq ≤ throughSeq</c>. Cleanup hook for
    /// when the bot acknowledges its inbound watermark via the next
    /// <c>Sequence</c> message it sends (G's responsibility to call).
    /// Idempotent.
    /// </summary>
    public void EvictUpTo(ulong throughSeq)
    {
        lock (_gate)
        {
            while (_entries.First is { } first && first.Value.Seq <= throughSeq)
            {
                _index.Remove(first.Value.Seq);
                _entries.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// Clears the buffer and resets the overflow flag. Called by the
    /// recovery path after the credential's <c>SessionVerId</c> has been
    /// bumped and the offending connection forcibly closed — the next
    /// reconnect attempt fails Establish with the new ver, the bot
    /// reconciles via REST, and we start fresh.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
            _index.Clear();
            _overflowed = false;
        }
    }

    private readonly record struct Entry(ulong Seq, byte[] Bytes);
}

/// <summary>
/// Outbound message returned by <see cref="BotOutboundBuffer.GetRange"/>.
/// </summary>
public readonly record struct BufferedOutboundMessage(ulong Seq, ReadOnlyMemory<byte> Bytes);
