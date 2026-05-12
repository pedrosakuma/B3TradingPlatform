using System.Buffers;

namespace B3.Trading.Application.UserBots;

/// <summary>
/// RFC §5.5 / issue #230. Stack-only carrier for a SOFH-framed outbound
/// application message together with optional ownership of a pooled
/// <c>byte[]</c>. Encoders rent from
/// <see cref="ArrayPool{T}.Shared"/>, write the frame in-place, and
/// hand the resulting <see cref="OutboundFrame"/> to
/// <see cref="BotOutboundBuffer.Append(ulong, OutboundFrame)"/>. From
/// that point on, the per-credential buffer is the <b>sole</b> owner of
/// the pooled array and is responsible for its eventual return to the
/// pool.
///
/// <para><b>Wrapper allocation elimination (issue #230, post P7).</b>
/// In P7 (#218) <c>OutboundFrame</c> was introduced as a <c>sealed
/// class</c> wrapping <c>(IMemoryOwner&lt;byte&gt;?, ReadOnlyMemory&lt;byte&gt;)</c>.
/// The combined per-encode overhead (the class + the
/// <c>ArrayMemoryPoolBuffer</c> the <see cref="MemoryPool{T}.Shared"/>
/// returns on every <c>Rent</c>) was ~64B. Issue #230 collapses both:
/// (a) turns the wrapper into a <c>readonly struct</c>, passed by
/// value through encoder → multiplexer → buffer / channel, stored
/// inline in container slots; (b) replaces the <c>MemoryPool</c> hop
/// with <see cref="ArrayPool{T}.Shared"/> directly, which returns the
/// <c>byte[]</c> without an owner wrapper. Net: 0B managed per encode
/// (the rented array itself comes from the pool's already-allocated
/// inventory and does not register as a per-call allocation).</para>
///
/// <para><b>Single-disposer rule (RFC §5.5 — non-negotiable).</b>
/// <list type="bullet">
///   <item>The buffer returns the <see cref="PooledArray"/> exactly
///         once, either when the frame's seq is evicted by an acked
///         watermark (<see cref="BotOutboundBuffer.EvictUpTo"/>) or
///         when an overflow / reset triggers a bulk clear.</item>
///   <item><c>TryEnqueue</c> to the live socket NEVER returns — it
///         only borrows <see cref="Bytes"/>.</item>
///   <item>The drain loop NEVER returns — same rule.</item>
///   <item>If <c>Append</c> rejects the frame (overflow / closed),
///         <c>Append</c> itself returns the pooled array before
///         returning <c>false</c>.</item>
///   <item>If a caller encodes but decides not to call <c>Append</c>,
///         it must release via <see cref="DisposeOwner"/> (test /
///         bench only; no production code path needs this today
///         because <c>Route</c> always reaches <c>Append</c>).</item>
/// </list>
/// Because the struct is passed by value, multiple copies of an
/// <see cref="OutboundFrame"/> may exist in flight (one on the
/// multiplexer's stack, one held by the buffer's <c>Entry</c>, one in
/// the channel writer's array). Single-disposer is preserved by
/// <b>policy</b>: only the per-credential buffer's stored copy is ever
/// returned, and the return is serialised under the buffer's internal
/// lock together with the entry's removal from the linked list, so no
/// returned-copy is reachable afterwards. Channel-side and drain-loop
/// copies never call <see cref="DisposeOwner"/>.</para>
///
/// <para>By design <c>OutboundFrame</c> is <b>not</b>
/// <see cref="IDisposable"/> — that prevents accidental
/// <c>using</c>-blocking the frame mid-pipeline. <see cref="PooledArray"/>
/// and <see cref="Pool"/> are <c>internal</c> to the
/// <c>B3.Trading.Application</c> assembly so only the buffer can touch
/// them; outside callers can only read <see cref="Bytes"/>.</para>
///
/// <para>An <see cref="Unowned"/> variant exists for tests and legacy
/// call sites whose bytes are not pooled (typically a heap-allocated
/// <c>byte[]</c>). The buffer treats unowned frames identically except
/// that there is nothing to return.</para>
/// </summary>
public readonly struct OutboundFrame
{
    /// <summary>The framed bytes ready for the socket.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Pooled <c>byte[]</c> backing <see cref="Bytes"/>, or <c>null</c>
    /// for unowned (heap-backed) frames. Internal so only the buffer
    /// assembly can hold it — see the single-disposer rule above.
    /// </summary>
    internal byte[]? PooledArray { get; }

    /// <summary>
    /// Array pool the <see cref="PooledArray"/> must be returned to,
    /// or <c>null</c> for unowned frames. Carried alongside the array
    /// so tests can swap in a tracking pool without static state.
    /// </summary>
    internal ArrayPool<byte>? Pool { get; }

    private OutboundFrame(byte[]? array, ArrayPool<byte>? pool, ReadOnlyMemory<byte> bytes)
    {
        PooledArray = array;
        Pool = pool;
        Bytes = bytes;
    }

    /// <summary>
    /// Wraps a freshly-rented <paramref name="array"/> together with
    /// the <paramref name="length"/>-byte slice the encoder filled in
    /// and the <paramref name="pool"/> it came from. The returned
    /// frame transfers ownership to whoever ultimately calls
    /// <see cref="BotOutboundBuffer.Append(ulong, OutboundFrame)"/>.
    /// </summary>
    public static OutboundFrame Pooled(byte[] array, int length, ArrayPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(pool);
        if ((uint)length > (uint)array.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        return new OutboundFrame(array, pool, new ReadOnlyMemory<byte>(array, 0, length));
    }

    /// <summary>
    /// Builds an unowned frame around an existing in-memory buffer
    /// (typically a heap-allocated <c>byte[]</c> from a test or a
    /// legacy non-pooled encode path). The buffer never returns
    /// anything for unowned frames; the caller guarantees the bytes
    /// remain valid for as long as the frame is buffered.
    /// </summary>
    public static OutboundFrame Unowned(ReadOnlyMemory<byte> bytes)
        => new(array: null, pool: null, bytes);

    /// <summary>
    /// Returns the pooled <see cref="PooledArray"/> to its
    /// <see cref="Pool"/>, if any. Invoked exclusively by
    /// <see cref="BotOutboundBuffer"/> when its hold on the frame ends
    /// (eviction / overflow / reset / rejected append), under the
    /// buffer's internal lock and immediately followed (in the
    /// eviction / reset paths) by removing the owning entry from the
    /// linked list. The buffer is the only code that ever calls this;
    /// channel-writer / drain-loop copies of the struct must not.
    /// </summary>
    internal void DisposeOwner()
    {
        if (PooledArray is { } array && Pool is { } pool)
        {
            // clearArray:false — outbound frames hold only public ER
            // wire bytes; no credentials or shared secrets that would
            // justify the per-return memset cost.
            pool.Return(array, clearArray: false);
        }
    }
}
