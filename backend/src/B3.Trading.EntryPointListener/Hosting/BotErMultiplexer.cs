using System.Threading.Channels;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #172 (F). Background drain that turns each
/// <see cref="ExecutionEvent"/> dispatched by
/// <see cref="ExecutionReportProcessor"/> into a per-bot SBE
/// ExecutionReport, allocates the next outbound seq from the
/// credential's allocator, encodes the SOFH-framed bytes, and
/// either pushes them onto the bot's live connection (when online)
/// or buffers them for retransmit (when offline).
///
/// <para>The drain is single-threaded by design — there is no
/// per-credential ordering requirement beyond "FIFO within a
/// credential" and a single drain trivially satisfies it without a
/// per-credential lock. If the channel ever becomes a throughput
/// bottleneck, the right move is to shard by credentialId across N
/// drain tasks (still preserving per-credential order); v0 keeps it
/// simple.</para>
///
/// <para><b>Overflow handling:</b> when the per-credential
/// <see cref="BotOutboundBuffer"/> hits its cap, its overflow callback
/// signals back into the multiplexer, which then asynchronously
/// invokes <see cref="IUserBotSessionRegistry.BumpVersionAsync"/> with
/// <c>reason="overflow"</c> and force-closes the offending sender.
/// The version-bump must happen BEFORE the close so the bot's
/// reconnect attempt fails Establish with the new ver
/// (<c>InvalidSessionVerId</c>) and the bot reconciles via REST
/// rather than silently observing a gap (RFC §4.7).</para>
/// </summary>
public sealed class BotErMultiplexer : BackgroundService, IBotErRouter
{
    private readonly IUserBotOrderMappingRegistry _mappings;
    private readonly IUserBotSessionRegistry _sessions;
    private readonly IBotSessionConnectionDirectory _directory;
    private readonly BotOutboundCoordinator _outbound;
    private readonly ILogger<BotErMultiplexer> _logger;
    private readonly Channel<ExecutionEvent> _eventChannel;
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
        _ = options; // RouterChannelCapacity is reserved for a future bounded variant; channel is currently unbounded — see comment below.

        // Unbounded so the synchronous Route() from the ER hot path
        // never blocks and never silently drops. Memory pressure is
        // bounded by the per-credential outbound buffer caps — when a
        // bot is offline its ERs accumulate in the Channel only briefly
        // (single drain pass) before landing in the per-credential
        // BotOutboundBuffer, which is the layer that observes overflow
        // and triggers the version-bump path. A bounded channel here
        // (DropOldest) would silently lose ERs without any sequence-gap
        // signal to the bot — see code-review concern (1).
        _eventChannel = Channel.CreateUnbounded<ExecutionEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
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
    public void Route(ExecutionEvent ev)
    {
        // Synchronous, non-blocking. The bounded channel's DropOldest
        // policy means a hung drain cannot stall the ER processor.
        _eventChannel.Writer.TryWrite(ev);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Two cooperating loops: one drains the event channel and
        // performs the routing/encode work synchronously; the other
        // handles overflow events out-of-band so the version-bump
        // FlushAsync does not stall the ER pipeline.
        var routeLoop = Task.Run(() => RunRouteLoopAsync(stoppingToken), stoppingToken);
        var overflowLoop = Task.Run(() => RunOverflowLoopAsync(stoppingToken), stoppingToken);
        await Task.WhenAll(routeLoop, overflowLoop).ConfigureAwait(false);
    }

    private async Task RunRouteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _eventChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    RouteOne(ev);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "fixp.outbound.route.error clOrdId={ClOrdId} kind={Kind}",
                        ev.ClOrdId, ev.Kind);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void RouteOne(ExecutionEvent ev)
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
            // memory; the sender only borrows the bytes. Eviction (or
            // overflow / reset) is what eventually disposes — never
            // TryEnqueue, never the drain loop.
            if (!sender.TryEnqueue(frame.Bytes))
            {
                // Race: the connection went away between TryGet and
                // TryEnqueue. The buffer already holds the message,
                // so retransmit (G) will pick it up on reconnect.
                _logger.LogDebug(
                    "fixp.outbound.send.race credentialId={CredentialId} seq={Seq}",
                    mapping.CredentialId, seq);
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
