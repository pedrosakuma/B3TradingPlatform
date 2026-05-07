namespace B3.Trading.Application;

/// <summary>
/// Outcome surfaced by the FIXP gateway after a successful reconnect cycle.
/// Drives the slice 2 (#132) auto-staleness policy.
/// </summary>
/// <param name="HadInboundGap">
/// True when the SDK raised <c>InboundGapAtReconnect</c> during the just-
/// completed <c>ReconnectAsync</c> call. Indicates an unrecovered
/// application-frame gap from the prior session — high-confidence "venue
/// state may diverge from ours".
/// </param>
/// <param name="GapFromSeq">First missing inbound seqnum, when <see cref="HadInboundGap"/>.</param>
/// <param name="GapCount">Length of the missing window, when <see cref="HadInboundGap"/>.</param>
/// <param name="PriorSessionVerId">SessionVerID of the prior session in which the gap occurred.</param>
/// <param name="PriorTerminationCode">
/// Stringified <c>TerminatedEventArgs.Code</c> from the most recent
/// peer-initiated terminate that triggered this reconnect, or <c>null</c>
/// if the reconnect was driven by something other than a peer terminate
/// (e.g. operator-driven). Used to discriminate transport blips from
/// venue-restart-class events.
/// </param>
public sealed record ReconnectOutcome(
    bool HadInboundGap,
    ulong? GapFromSeq,
    uint? GapCount,
    ulong? PriorSessionVerId,
    string? PriorTerminationCode);

/// <summary>
/// Slice 2 (#132) seam: lets the FIXP gateway hand off post-reconnect
/// venue-divergence signals to the staleness layer without taking a
/// reference to <see cref="OrderStalenessService"/> directly. The
/// implementation lives in Application and applies the policy described
/// on <see cref="OrderStaleningVenueReactor"/>.
/// </summary>
public interface IVenueDisconnectReactor
{
    /// <summary>
    /// Called by the gateway on the firm's reconnect-loop background task
    /// after a successful FIXP reconnect (post-Establish). Implementation
    /// MUST be idempotent and tolerate being called once per reconnect
    /// attempt — no state machine on the caller side.
    /// </summary>
    void OnPeerReconnected(string firmId, ReconnectOutcome outcome);
}

/// <summary>
/// Default <see cref="IVenueDisconnectReactor"/> implementation that
/// translates the slice 2 (#132) policy into bulk-staleness writes:
///
/// <list type="bullet">
///   <item>If the SDK raised <c>InboundGapAtReconnect</c>, mark every
///         working order for the firm as stale with reason
///         <c>inbound_gap:{from}-{to}</c>. This is the high-confidence
///         signal — peer's <c>SessionVerID</c> bump means the missing
///         range is unrecoverable in-band, so the venue's view of our
///         working set may have moved out from under us.</item>
///   <item>If there was no inbound gap and the previous session ended
///         with a peer-initiated terminate, only mark stale when the
///         <c>AutoStaleOnPeerTerminate</c> flag is enabled
///         (<see cref="AutoStaleOptions.OnPeerTerminate"/>). Default
///         <c>false</c>, because routine network blips also produce
///         peer-initiated terminates and we don't want every transport
///         hiccup to ghost the firm's whole working set.</item>
/// </list>
///
/// <para>
/// Auto-clear (slice 1) lifts the flag for any order the venue still
/// knows about as soon as a terminal ER arrives, so a false positive
/// here is self-healing for fully-filled / cancelled / rejected orders.
/// PartiallyFilled orders need an operator clear.
/// </para>
/// </summary>
public sealed class OrderStaleningVenueReactor : IVenueDisconnectReactor
{
    private readonly OrderStalenessService _staleness;
    private readonly TimeProvider _clock;
    private readonly AutoStaleOptions _options;

    public OrderStaleningVenueReactor(
        OrderStalenessService staleness,
        AutoStaleOptions options,
        TimeProvider? clock = null)
    {
        _staleness = staleness;
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public void OnPeerReconnected(string firmId, ReconnectOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("firmId required.", nameof(firmId));

        string? reason = null;
        if (outcome.HadInboundGap && outcome.GapFromSeq is { } from && outcome.GapCount is { } count)
        {
            // High-confidence: surface seq window in the reason so the
            // operator can correlate against SDK logs / per-firm WAL.
            var to = from + count - 1;
            reason = $"inbound_gap:{from}-{to}";
        }
        else if (!outcome.HadInboundGap && _options.OnPeerTerminate
                 && !string.IsNullOrWhiteSpace(outcome.PriorTerminationCode))
        {
            reason = $"peer_terminated:{outcome.PriorTerminationCode}";
        }

        if (reason is null) return;

        var marked = _staleness.MarkAllWorkingByFirm(firmId, reason, _clock.GetUtcNow(), actorUserId: null);
        if (marked > 0)
        {
            Observability.MetricsRegistry.OrdersAutoStaledByVenueDesync.Add(marked,
                new KeyValuePair<string, object?>("firm", firmId),
                new KeyValuePair<string, object?>("reason", reason));
        }
    }
}

/// <summary>
/// Bound to <c>Trading:AutoStale</c> in configuration. Controls the
/// slice 2 (#132) auto-staleness policy on the <see cref="OrderStaleningVenueReactor"/>.
/// </summary>
public sealed class AutoStaleOptions
{
    public const string SectionName = "Trading:AutoStale";

    /// <summary>
    /// When <c>true</c>, peer-initiated terminations (FIXP <c>Terminate</c>
    /// from the venue) trigger a bulk stale-mark even when the SDK does NOT
    /// raise an inbound gap. Default <c>false</c>: routine transport
    /// hiccups also produce peer terminates and would otherwise ghost the
    /// whole firm's working set on every blip. Enable for venues whose
    /// terminate codes reliably indicate state loss.
    /// </summary>
    public bool OnPeerTerminate { get; set; }
}
