using B3.Trading.Application.UserBots;
using Microsoft.Extensions.Options;

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
    /// Checkpoint cadence (RFC §4.8): every N seconds OR every M
    /// messages, whichever comes first. Either both at default (5s,
    /// 100msg) or operator-tuned.
    /// </summary>
    public TimeSpan CheckpointPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-credential message count threshold that forces an early checkpoint.</summary>
    public int CheckpointMessageThreshold { get; set; } = 100;
}

public sealed class BotErMultiplexerOptionsValidator : IValidateOptions<BotErMultiplexerOptions>
{
    public ValidateOptionsResult Validate(string? name, BotErMultiplexerOptions options)
    {
        var failures = new List<string>();
        if (options.OutboundBufferMaxMessages <= 0)
            failures.Add("Trading:EntryPointListener:Outbound:OutboundBufferMaxMessages must be > 0.");
        if (options.CheckpointPeriod <= TimeSpan.Zero)
            failures.Add("Trading:EntryPointListener:Outbound:CheckpointPeriod must be > 0.");
        if (options.CheckpointMessageThreshold <= 0)
            failures.Add("Trading:EntryPointListener:Outbound:CheckpointMessageThreshold must be > 0.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
