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
    /// Capacity of the in-process channel between
    /// <see cref="ExecutionReportProcessor"/> and the routing loop.
    /// DropOldest on full so the ER processor is never stalled.
    /// </summary>
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
