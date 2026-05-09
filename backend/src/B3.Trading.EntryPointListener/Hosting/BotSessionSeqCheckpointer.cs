using B3.Trading.Application.UserBots;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #172 (F). Background checkpointer that periodically
/// records the credential's outbound seq watermark to WAL via
/// <see cref="IUserBotSessionRegistry.UpdateCheckpointedOutboundSeq"/>.
///
/// <para>RFC §4.8 cadence: every <see cref="BotErMultiplexerOptions.CheckpointPeriod"/>
/// OR per-credential <see cref="BotErMultiplexerOptions.CheckpointMessageThreshold"/>
/// outbound messages, whichever comes first. The threshold is checked
/// each time we wake; it does NOT preempt the timer (good enough for a
/// best-effort durability watermark — the timer guarantees we never go
/// longer than the period without a checkpoint).</para>
///
/// <para>No checkpoint event is dispatched when the per-credential
/// counter is zero (RFC §4.10 — keeps the WAL spam-free during quiet
/// periods).</para>
/// </summary>
public sealed class BotSessionSeqCheckpointer : BackgroundService
{
    private readonly BotOutboundCoordinator _coordinator;
    private readonly IUserBotSessionRegistry _sessions;
    private readonly BotErMultiplexerOptions _opts;
    private readonly ILogger<BotSessionSeqCheckpointer> _logger;
    private readonly TimeProvider _clock;

    public BotSessionSeqCheckpointer(
        BotOutboundCoordinator coordinator,
        IUserBotSessionRegistry sessions,
        IOptions<BotErMultiplexerOptions> opts,
        ILogger<BotSessionSeqCheckpointer> logger,
        TimeProvider? clock = null)
    {
        _coordinator = coordinator;
        _sessions = sessions;
        _opts = opts.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = _opts.CheckpointPeriod;
        // Fast-poll cadence for the message-count threshold: 100ms is
        // small enough that a high-rate bot's 100-msg threshold is hit
        // ~within 100ms of the message that crossed it, but coarse
        // enough to be cheap (10 wakeups/sec/host even when idle). The
        // periodic full-tick is on the longer cadence so the WAL stays
        // quiet during low-rate operation.
        var fastPoll = TimeSpan.FromMilliseconds(100);
        var nextFullTick = _clock.GetUtcNow() + period;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(fastPoll, _clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                var now = _clock.GetUtcNow();
                if (now >= nextFullTick)
                {
                    Tick();
                    nextFullTick = now + period;
                }
                else if (_opts.CheckpointMessageThreshold > 0)
                {
                    TickThresholdOnly();
                }
            }
        }
        finally
        {
            // Final pass on shutdown so a terminating host emits the most
            // recent watermark before the WAL is closed.
            try { Tick(); } catch (Exception ex) { _logger.LogDebug(ex, "fixp.checkpoint.shutdown.error"); }
        }
    }

    /// <summary>
    /// Public for tests. Iterates active credentials and dispatches a
    /// <c>BotSessionSeqAdvancedEvent</c> per credential whose counter
    /// is non-zero, then resets the counter under the registry's lock.
    /// </summary>
    public void Tick() => CheckpointActive(thresholdOnly: false);

    /// <summary>
    /// Inter-tick fast-pass: only checkpoints credentials whose counter
    /// has crossed <see cref="BotErMultiplexerOptions.CheckpointMessageThreshold"/>
    /// since the last checkpoint. Lets a high-rate credential get a
    /// fresh watermark inside the period without WAL-spamming quiet
    /// credentials on every fast wakeup.
    /// </summary>
    public void TickThresholdOnly() => CheckpointActive(thresholdOnly: true);

    private void CheckpointActive(bool thresholdOnly)
    {
        var credentials = _coordinator.ListActiveCredentials();
        var threshold = _opts.CheckpointMessageThreshold;
        foreach (var credentialId in credentials)
        {
            if (thresholdOnly && _coordinator.GetCounter(credentialId) < threshold)
                continue;

            var counter = _coordinator.ReadAndResetCounter(credentialId);
            if (counter <= 0)
                continue;
            var watermark = _coordinator.GetCurrentSeq(credentialId);
            try
            {
                _sessions.UpdateCheckpointedOutboundSeq(credentialId, watermark);
                _logger.LogDebug(
                    "fixp.checkpoint credentialId={CredentialId} seq={Seq} since={Counter} reason={Reason}",
                    credentialId, watermark, counter, thresholdOnly ? "threshold" : "period");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fixp.checkpoint.error credentialId={CredentialId}", credentialId);
            }
        }
    }
}
