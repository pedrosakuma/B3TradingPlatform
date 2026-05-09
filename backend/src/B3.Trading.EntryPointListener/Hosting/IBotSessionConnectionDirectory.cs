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
    /// already registered, it is evicted (caller's responsibility — by
    /// the time we get here, the squatter-kick or version-bump path has
    /// already terminated the prior connection's session-level state).
    /// </summary>
    void Register(Guid credentialId, IBotSessionOutboundSender sender);

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
}

/// <summary>
/// Outbound side of a live FIXP connection. The multiplexer pushes
/// pre-framed SOFH bytes here; the implementation is responsible for
/// serialising writes against any concurrent handshake/order-ack
/// writes coming from the connection's own request loop.
/// </summary>
public interface IBotSessionOutboundSender
{
    /// <summary>
    /// Synchronously enqueues <paramref name="framedBytes"/> for send.
    /// Implementations buffer + flush on a background task; this method
    /// must NOT block on socket I/O so the caller (the ER multiplexer
    /// drain loop) is not coupled to network latency.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the bytes were accepted; <c>false</c> when the
    /// sender is closed (the connection went away between the directory
    /// lookup and the enqueue — the multiplexer falls back to buffering
    /// for retransmit).
    /// </returns>
    bool TryEnqueue(ReadOnlyMemory<byte> framedBytes);
}
