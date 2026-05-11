using System.Threading.Channels;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #172 (F) / RFC §5.4 (P9, F4). Turns each
/// <see cref="ExecutionEvent"/> dispatched by
/// <see cref="ExecutionReportProcessor"/> into a per-bot SBE
/// ExecutionReport, allocates the next outbound seq from the
/// credential's allocator, encodes the SOFH-framed bytes, and
/// either pushes them onto the bot's live connection (when online)
/// or buffers them for retransmit (when offline).
///
/// <para><b>P9 / F4 — synchronous credential resolve.</b> There is no
/// global multiplexer channel and no router drain thread. <see cref="Route"/>
/// and the <see cref="IExecutionFanOutSink.Enqueue"/> hook resolve the
/// originating credential synchronously (a lock-free
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// hit on <see cref="IUserBotOrderMappingRegistry"/>) and dispatch
/// directly into the per-credential <see cref="BotOutboundBuffer"/> +
/// per-connection writer channel (P8). The pre-P9 unbounded
/// <c>Channel&lt;ExecutionEvent&gt;</c> and its single-reader drain
/// loop are gone: they were the lossy global hop that made memory
/// growth single-bot-bound (one slow consumer queued every ER for
/// every credential), and bounding that channel would have silently
/// dropped ERs at a point where the credential was not yet known and
/// no per-bot recovery signal could be emitted (RFC §5.4 / §6.3).</para>
///
/// <para><b>Sole bounded layer.</b> Backpressure is concentrated in
/// two places: the per-credential <see cref="BotOutboundBuffer"/>
/// (cap → version-bump + force-close, RFC §4.7) and the
/// per-connection writer channel (P8 / RFC §5.3.1, full → leave the
/// frame in the buffer for retransmit on the next reconnect). No
/// unbounded queue separates a slow credential from any other
/// credential — slow-credential isolation is by construction.</para>
///
/// <para><b>Per-credential ordering (RFC §4.3).</b>
/// <see cref="IExecutionFanOutSink.Enqueue"/> is invoked UNDER the
/// dispatcher lock (see <see cref="EventDispatcher"/>), so the
/// `seq → resolve → buffer.Append → sender.TryEnqueue` chain runs
/// in WAL append order. The per-credential allocator and the
/// per-credential buffer's internal lock then carry that order all
/// the way to the per-connection FIFO writer channel (P8). The
/// legacy <see cref="Route"/> entry point — used by tests that drive
/// <see cref="ExecutionReportProcessor"/> without a dispatcher — has
/// the same guarantee per single producer thread.</para>
///
/// <para><b>Overflow handling.</b> When the per-credential buffer
/// hits its cap, its overflow callback signals back into the
/// multiplexer, which posts the credentialId to a small
/// (credentialId-only) overflow channel drained out-of-band. The
/// drain calls <see cref="IUserBotSessionRegistry.BumpVersionAsync"/>
/// with <c>reason="overflow"</c> and force-closes the offending
/// sender. The version-bump must happen BEFORE the close so the
/// bot's reconnect attempt fails Establish with the new ver
/// (<c>InvalidSessionVerId</c>) and the bot reconciles via REST
/// rather than silently observing a gap (RFC §4.7). Async work
/// stays out-of-band because the synchronous resolve path runs
/// under the dispatcher lock and MUST NOT block.</para>
/// </summary>
public sealed class BotErMultiplexer : BackgroundService, IBotErRouter, IExecutionFanOutSink
{
    private readonly IUserBotOrderMappingRegistry _mappings;
    private readonly IUserBotSessionRegistry _sessions;
    private readonly IBotSessionConnectionDirectory _directory;
    private readonly BotOutboundCoordinator _outbound;
    private readonly ILogger<BotErMultiplexer> _logger;
    private readonly Channel<Guid> _overflowChannel;

    public BotErMultiplexer(
        IUserBotOrderMappingRegistry mappings,
        IUserBotSessionRegistry sessions,
        IBotSessionConnectionDirectory directory,
        BotOutboundCoordinator outbound,
        ILogger<BotErMultiplexer> logger,
        IOptions<BotErMultiplexerOptions>? options = null)
    {
        _mappings = mappings;
        _sessions = sessions;
        _directory = directory;
        _outbound = outbound;
        _logger = logger;
        _ = options; // P9 (F4) removed the global router channel; RouterChannelCapacity is retained on the options type only for backwards-compatible config binding (see BotErMultiplexerOptions).

        // CredentialId-only overflow signal channel. Unbounded is safe
        // here because the message is a 16-byte Guid and the post rate
        // is bounded by the number of credentials (one signal per
        // credential per overflow → BumpVersion cycle, not by the ER
        // rate). The route hot path posts here from inside the
        // BotOutboundBuffer overflow callback (under the buffer's
        // internal lock), so the post itself MUST be non-blocking.
        _overflowChannel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Wire the buffer's overflow callback once. The coordinator
        // creates buffers lazily per credential, each carrying our
        // OnOverflow.
        _outbound.OnBufferOverflow = credentialId =>
        {
            // Best-effort enqueue. If the overflow channel itself is
            // backlogged we still log — overflow handling is rare and
            // a missed signal would just delay the inevitable Bump on
            // the next router pass.
            if (!_overflowChannel.Writer.TryWrite(credentialId))
            {
                _logger.LogWarning(
                    "fixp.outbound.overflow.signal-dropped credentialId={CredentialId}",
                    credentialId);
            }
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// RFC §5.4 (P9 / F4). Synchronous credential resolve + direct
    /// dispatch into the per-credential <see cref="BotOutboundBuffer"/>
    /// and per-connection writer (P8). No async hop, no global queue.
    /// Used by tests / non-dispatcher call sites in
    /// <see cref="ExecutionReportProcessor"/>; the production fan-out
    /// path goes through <see cref="IExecutionFanOutSink.Enqueue"/>
    /// (called under the dispatcher lock).
    /// </remarks>
    public void Route(ExecutionEvent ev) => RouteOne(ev);

    /// <inheritdoc />
    public ExecutionFanOutTargets Target => ExecutionFanOutTargets.BotRouter;

    /// <inheritdoc />
    /// <remarks>
    /// RFC §5.2 (F2) + §5.4 (F4). The dispatcher invokes this UNDER
    /// the dispatcher lock so the resolve / encode / append /
    /// per-connection-enqueue chain runs in strict WAL seq order. All
    /// work is non-blocking and synchronous: <see cref="IUserBotOrderMappingRegistry.TryGetOrderMapping"/>
    /// is a lock-free dictionary hit, <see cref="BotOutboundBuffer.Append"/>
    /// only takes a short per-credential lock, and the per-connection
    /// <see cref="IBotSessionOutboundSender.TryEnqueue"/> is a
    /// non-blocking <c>Channel.TryWrite</c> (P8). <paramref name="seq"/>
    /// is captured for diagnostics only — the encoder uses the
    /// per-credential outbound seq allocator, not the WAL seq.
    /// </remarks>
    void IExecutionFanOutSink.Enqueue(long seq, ExecutionEvent ev)
    {
        _ = seq;
        RouteOne(ev);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Post-P9 there is no router drain — only the out-of-band
        // overflow handler, whose async BumpVersion+close work cannot
        // run inline under the dispatcher lock.
        return Task.Run(() => RunOverflowLoopAsync(stoppingToken), stoppingToken);
    }

    private void RouteOne(ExecutionEvent ev)
    {
        try
        {
            RouteOneCore(ev);
        }
        catch (Exception ex)
        {
            // The route path runs under the dispatcher lock; an
            // unhandled throw would tear down the entire ER pipeline
            // and (worse) block any other dispatch waiting on the
            // lock. Swallow and log — a single bot's encode failure
            // must not stall every other bot.
            _logger.LogError(ex,
                "fixp.outbound.route.error clOrdId={ClOrdId} kind={Kind}",
                ev.ClOrdId, ev.Kind);
        }
    }

    private void RouteOneCore(ExecutionEvent ev)
    {
        if (!_mappings.TryGetOrderMapping(ev.ClOrdId, out var mapping))
        {
            // REST/WS-origin order — no bot to forward to. Today's behavior.
            return;
        }

        // Cancel/replace ERs reference an OrigClOrdID. ExecutionEvent
        // has already been normalized to the *original* internal id by
        // ExecutionReportProcessor (cs:95-109), so the forward mapping
        // we just resolved IS the original. The cancel-side mapping
        // would carry the bot's separate cancel ClOrdID — F's v0 does
        // not yet correlate the raw cancel-side id (sub-issue G when
        // raw side-channel lands), so we omit it for cancel/replace
        // and the bot reads OrigClOrdID = its original ClOrdID.
        ulong externalOrig = (ev.Kind is ExecKind.Canceled or ExecKind.Replaced)
            ? mapping.ExternalClOrdId
            : 0UL;

        OutboundFrame frame;
        try
        {
            frame = OutboundExecutionReportEncoder.Encode(ev, mapping.ExternalClOrdId, externalOrig);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex,
                "fixp.outbound.encode.unsupported credentialId={CredentialId} kind={Kind}",
                mapping.CredentialId, ev.Kind);
            return;
        }

        var seq = _outbound.AllocateNext(mapping.CredentialId);

        // Buffer FIRST, then attempt send. This ordering matters for
        // the overflow → version-bump sequence: if the buffer Append
        // trips overflow, we must not also push the message onto a
        // live connection — the bot would observe an ER and then a
        // version bump that effectively rolled it back.
        //
        // Append takes ownership of `frame`'s pooled memory on success
        // (RFC §5.5 single-disposer rule). If it returns false it has
        // already disposed the rejected frame on our behalf — we must
        // NOT touch frame.Bytes after a false return.
        var buffer = _outbound.GetOrCreateBuffer(mapping.CredentialId);
        var accepted = buffer.Append(seq, frame);
        _outbound.RecordOutbound(mapping.CredentialId);

        if (!accepted)
        {
            // Overflow — the buffer's callback already enqueued an
            // overflow signal. Skip the send.
            _logger.LogWarning(
                "fixp.outbound.buffer.overflow credentialId={CredentialId} clOrdId={ClOrdId} seq={Seq}",
                mapping.CredentialId, ev.ClOrdId, seq);
            return;
        }

        if (_directory.TryGet(mapping.CredentialId, out var sender))
        {
            // The buffer is now the sole owner of frame's pooled
            // memory; the sender only borrows the bytes via the
            // per-connection drain loop. Eviction (or overflow / reset)
            // is what eventually disposes — never TryEnqueue, never
            // the drain loop (RFC §5.3 / §5.5).
            if (!sender.TryEnqueue(frame))
            {
                // RFC §5.3.1 / §5.4: per-session writer-channel
                // backpressure (P8 channel full) MUST trigger the
                // version-bump path. The frame is already in the
                // per-credential buffer so retransmit can replay it,
                // but every subsequent successfully-enqueued ER would
                // carry a higher per-credential outbound seq — the bot
                // would observe an N+1 on the wire without ever seeing
                // N. Without a forced reconnect (with bumped ver), the
                // gap is silent. Signal the same out-of-band overflow
                // handler the buffer-cap path uses: BumpVersion +
                // force-close + Reset. The signal is a Guid only and
                // the channel is unbounded; the credential-rate cap
                // (one signal per credential per overflow→bump cycle)
                // is enforced by the buffer's own _overflowed gate
                // once the cleared buffer goes back into Append-reject
                // mode after this frame.
                //
                // We also trigger this on a TryGet/TryEnqueue race
                // (sender removed between calls, or sender already
                // disposed): the cost is one redundant version bump
                // for a bot that will reconnect anyway, which is
                // strictly safer than a silent gap.
                _logger.LogWarning(
                    "fixp.outbound.send.backpressure-or-race credentialId={CredentialId} seq={Seq}",
                    mapping.CredentialId, seq);
                if (!_overflowChannel.Writer.TryWrite(mapping.CredentialId))
                {
                    _logger.LogWarning(
                        "fixp.outbound.overflow.signal-dropped credentialId={CredentialId}",
                        mapping.CredentialId);
                }
            }
        }
        // else: bot offline, message is buffered for G's retransmit.
    }

    private async Task RunOverflowLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var credentialId in _overflowChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleOverflowAsync(credentialId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "fixp.outbound.overflow.handle.error credentialId={CredentialId}",
                        credentialId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task HandleOverflowAsync(Guid credentialId, CancellationToken ct)
    {
        // RFC §4.5 / §4.7 ordering: bump version FIRST (with the
        // FlushAsync fence inside BumpVersionAsync) so the bot's
        // reconnect must observe the new ver. Only after the durable
        // bump do we evict the in-flight sender so any racing TryEnqueue
        // returns false and the corresponding ER falls into the cleared
        // buffer (no replay, no rollback).
        var newVer = await _sessions.BumpVersionAsync(credentialId, "overflow", ct).ConfigureAwait(false);
        _logger.LogWarning(
            "fixp.outbound.overflow.bump credentialId={CredentialId} newVer={NewVer}",
            credentialId, newVer);

        if (_directory.TryGet(credentialId, out var sender))
        {
            // Force-close the live connection. The directory deregister
            // happens on the connection's own close path (finally block
            // of FixpSessionConnection.RunAsync) — calling Close here
            // makes the read loop unblock with 0 bytes / IOException.
            try
            {
                if (sender is IDisposable d) d.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "fixp.outbound.overflow.close.error credentialId={CredentialId}", credentialId);
            }
        }

        // Reset the buffer so the next legitimate session starts clean.
        // The reset must come AFTER the bump+close so a still-running
        // TryEnqueue from another router pass sees the cleared buffer
        // and returns false, not a partial state.
        _outbound.GetOrCreateBuffer(credentialId).Reset();
    }
}
