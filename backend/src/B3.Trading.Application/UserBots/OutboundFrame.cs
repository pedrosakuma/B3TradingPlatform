using System.Buffers;

namespace B3.Trading.Application.UserBots;

/// <summary>
/// RFC §5.5 (P7 / F5). Wraps a SOFH-framed outbound application
/// message together with optional ownership of the pooled memory it
/// lives in. Encoders rent from a <see cref="MemoryPool{T}"/>, write
/// the frame in-place, and hand the resulting <see cref="OutboundFrame"/>
/// to <see cref="BotOutboundBuffer.Append"/>. From that point on, the
/// per-credential buffer is the <b>sole</b> owner of the pooled memory
/// and is responsible for its eventual disposal.
///
/// <para><b>Single-disposer rule (RFC §5.5 — non-negotiable).</b>
/// <list type="bullet">
///   <item>The buffer disposes the <see cref="Owner"/> exactly once,
///         either when the frame's seq is evicted by an acked
///         watermark (<see cref="BotOutboundBuffer.EvictUpTo"/>) or
///         when an overflow / reset triggers a bulk clear.</item>
///   <item><c>TryEnqueue</c> to the live socket NEVER disposes — it
///         only borrows <see cref="Bytes"/>.</item>
///   <item>The drain loop NEVER disposes — same rule.</item>
///   <item>If <c>Append</c> rejects the frame (overflow / closed),
///         <c>Append</c> itself disposes before returning.</item>
///   <item>If a caller encodes but decides not to call <c>Append</c>,
///         it must dispose the owner via <see cref="DisposeOwner"/>
///         (test-only; no production code path needs this today
///         because <c>Route</c> always reaches <c>Append</c>).</item>
/// </list></para>
///
/// <para>By design <c>OutboundFrame</c> is <b>not</b>
/// <see cref="IDisposable"/> — that prevents accidental
/// <c>using</c>-blocking the frame mid-pipeline and double-freeing the
/// pooled buffer. The <see cref="Owner"/> getter is <c>internal</c> to
/// the <c>B3.Trading.Application</c> assembly so only the buffer can
/// touch it; outside callers can only read <see cref="Bytes"/>.</para>
///
/// <para>An <see cref="Unowned"/> variant exists for tests and legacy
/// call sites whose bytes are not pooled (typically a heap-allocated
/// <c>byte[]</c>). The buffer treats unowned frames identically except
/// that there is nothing to dispose.</para>
/// </summary>
public sealed class OutboundFrame
{
    private IMemoryOwner<byte>? _owner;

    /// <summary>The framed bytes ready for the socket.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Pooled-memory owner, or <c>null</c> when the frame is unowned
    /// (heap-backed). Internal so only the buffer assembly can hold it
    /// — see the single-disposer rule above.
    /// </summary>
    internal IMemoryOwner<byte>? Owner => _owner;

    private OutboundFrame(IMemoryOwner<byte>? owner, ReadOnlyMemory<byte> bytes)
    {
        _owner = owner;
        Bytes = bytes;
    }

    /// <summary>
    /// Wraps a freshly-rented <paramref name="owner"/> together with the
    /// <paramref name="length"/>-byte slice the encoder filled in. The
    /// returned frame transfers ownership to whoever ultimately calls
    /// <see cref="BotOutboundBuffer.Append"/>.
    /// </summary>
    public static OutboundFrame Pooled(IMemoryOwner<byte> owner, int length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if ((uint)length > (uint)owner.Memory.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        return new OutboundFrame(owner, owner.Memory[..length]);
    }

    /// <summary>
    /// Builds an unowned frame around an existing in-memory buffer
    /// (typically a heap-allocated <c>byte[]</c> from a test or a
    /// legacy non-pooled encode path). The buffer never disposes
    /// anything for unowned frames; the caller guarantees the bytes
    /// remain valid for as long as the frame is buffered.
    /// </summary>
    public static OutboundFrame Unowned(ReadOnlyMemory<byte> bytes)
        => new(owner: null, bytes);

    /// <summary>
    /// Releases the pooled <see cref="Owner"/>, if any. Invoked
    /// exclusively by <see cref="BotOutboundBuffer"/> when its hold
    /// on the frame ends (eviction / overflow / reset / rejected
    /// append). Idempotent.
    /// </summary>
    internal void DisposeOwner()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.Dispose();
    }
}
