using B3.Trading.Application.UserBots;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #172 (F). Per-credential lookup of the active FIXP
/// connection's outbound sender. Populated by
/// <see cref="FixpSessionConnection"/> on Establish-success and removed
/// on Terminate or socket-close. Thread-safe — touched both by the
/// listener accept/close path and by the ER multiplexer's hot path.
/// </summary>
public interface IBotSessionConnectionDirectory
{
    /// <summary>
    /// Registers <paramref name="sender"/> as the active outbound channel
    /// for <paramref name="credentialId"/>. If a different sender is
    /// already registered, the directory atomically publishes the replacement
    /// and closes the displaced sender.
    /// </summary>
    void Register(
        Guid credentialId,
        string connectionId,
        IBotSessionOutboundSender sender);

    /// <summary>
    /// Removes <paramref name="sender"/> from the directory, but only if
    /// it is still the active sender for <paramref name="credentialId"/>.
    /// Idempotent — deregistering a sender that has already been
    /// replaced by a newer connection is a no-op.
    /// </summary>
    void Deregister(Guid credentialId, IBotSessionOutboundSender sender);

    /// <summary>
    /// Returns the active sender for <paramref name="credentialId"/>, or
    /// <c>false</c> when the bot is offline. Hot-path lookup from the
    /// ER multiplexer.
    /// </summary>
    bool TryGet(Guid credentialId, out IBotSessionOutboundSender sender);

    /// <summary>
    /// Atomically removes and closes the currently registered connection for
    /// <paramref name="credentialId"/>. Used after a durable session-version
    /// bump invalidates the old lease.
    /// </summary>
    bool TryForceTerminate(Guid credentialId);

    /// <summary>
    /// Removes and closes the connection only when it still matches
    /// <paramref name="connectionId"/>. A newer replacement is left intact.
    /// </summary>
    bool TryForceTerminate(Guid credentialId, string connectionId);
}

/// <summary>
/// Outbound side of a live FIXP connection. The multiplexer pushes
/// pre-framed SOFH SBE messages here; the implementation owns a
/// per-connection bounded channel + dedicated drain loop (RFC §5.3 /
/// P8 / F3) that serialises writes against any concurrent
/// handshake/order-ack writes coming from the connection's own request
/// loop. <c>TryEnqueue</c> never blocks on socket I/O.
///
/// <para><b>Single-disposer of pooled outbound memory (RFC §5.5).</b>
/// The <see cref="OutboundFrame"/> handed in here is owned by the
/// per-credential <see cref="BotOutboundBuffer"/> (the caller appended
/// it before reaching us). Implementations only borrow
/// <see cref="OutboundFrame.Bytes"/> for the socket write — neither
/// the channel writer nor the drain loop ever calls
/// <c>DisposeOwner</c>. The buffer releases the pooled owner on
/// <c>EvictUpTo</c> / overflow / <c>Reset</c>.</para>
///
/// <para><b>Lifetime safety.</b> A bot can only ack a watermark for
/// sequences it has actually received, so an unsent (still-queued)
/// frame's seq cannot be evicted under us. Overflow / version-bump
/// force-closes the connection BEFORE the buffer's Reset clears
/// pooled owners, which terminates the drain loop. Both invariants
/// together let the drain loop hold the OutboundFrame across an
/// awaited <c>WriteAsync</c> without a defensive heap copy.</para>
/// </summary>
public interface IBotSessionOutboundSender
{
    /// <summary>
    /// Synchronously enqueues <paramref name="frame"/> for send onto
    /// the per-connection bounded outbound channel. Non-blocking.
    /// Returns <c>false</c> when (a) the sender is closed (the
    /// connection went away between the directory lookup and this
    /// call) or (b) the channel is full — the documented backpressure
    /// surface for a slow consumer (RFC §5.3.1). In both refused
    /// branches the frame remains owned by the per-credential
    /// <see cref="BotOutboundBuffer"/> and is replayed via
    /// retransmit (sub-issue G) on the next reconnect; this method
    /// itself never disposes pooled memory.
    /// </summary>
    bool TryEnqueue(OutboundFrame frame);
}
