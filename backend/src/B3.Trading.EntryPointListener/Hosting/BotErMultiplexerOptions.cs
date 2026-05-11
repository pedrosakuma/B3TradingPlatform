using B3.Trading.Application.UserBots;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #172 (F). Tunables for the bot outbound multiplexer +
/// checkpointer. All values have safe defaults; production deployments
/// override from <c>appsettings.json</c>.
/// </summary>
public sealed class BotErMultiplexerOptions
{
    public const string SectionName = "Trading:EntryPointListener:Outbound";

    /// <summary>
    /// Per-credential outbound buffer cap. Hitting this cap fires the
    /// overflow path (BumpVersion + force-close). Default <see cref="BotOutboundBuffer.DefaultMaxMessages"/>.
    /// </summary>
    public int OutboundBufferMaxMessages { get; set; } = BotOutboundBuffer.DefaultMaxMessages;

    /// <summary>
    /// <b>Deprecated (P9 / RFC §5.4).</b> Pre-P9, this sized the global
    /// <c>Channel&lt;ExecutionEvent&gt;</c> between the dispatcher and
    /// the multiplexer drain thread. P9 / F4 removed that channel —
    /// credential resolve is now synchronous in the fan-out path and
    /// the per-credential <see cref="BotOutboundBuffer"/> is the sole
    /// bounded layer (with <see cref="OutboundBufferMaxMessages"/> the
    /// only knob for ER-rate backpressure). The property is retained
    /// solely for backwards-compatible binding of existing
    /// <c>appsettings.json</c> sections so deployment configs do not
    /// fail to load; the value is ignored.
    /// </summary>
    [Obsolete("Removed in P9 / RFC §5.4 — synchronous credential resolve eliminated the global router channel. Tune OutboundBufferMaxMessages instead.")]
    public int RouterChannelCapacity { get; set; } = 16_384;

    /// <summary>
    /// Checkpoint cadence (RFC §4.8): every N seconds OR every M
    /// messages, whichever comes first. Either both at default (5s,
    /// 100msg) or operator-tuned.
    /// </summary>
    public TimeSpan CheckpointPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-credential message count threshold that forces an early checkpoint.</summary>
    public int CheckpointMessageThreshold { get; set; } = 100;
}
