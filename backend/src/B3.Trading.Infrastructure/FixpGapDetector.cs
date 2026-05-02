namespace B3.Trading.Infrastructure;

/// <summary>
/// Result of a single inbound seqnum observation.
/// </summary>
public enum GapObservation
{
    /// <summary>First event since the last reset; nothing to compare against.</summary>
    First,
    /// <summary>Strictly +1 from the previous seqnum — happy path.</summary>
    InOrder,
    /// <summary>Incoming &lt; (last+1). Either a duplicate or out-of-order replay.</summary>
    Duplicate,
    /// <summary>Incoming &gt; (last+1). At least one inbound message was lost.</summary>
    Gap,
}

/// <summary>
/// Pure function over inbound FIXP message seqnums. Independent of the SDK so
/// it can be unit-tested without driving <c>EntryPointClient</c>.
///
/// <para>
/// The SDK runs its own gap-recovery via <c>IRetransmitRequestHandler</c>;
/// this detector is a defensive "did the SDK actually deliver everything in
/// order?" check feeding the <c>trading.entrypoint.gap_detected</c> metric.
/// We do <i>not</i> drive a reconnect from here — the SDK owns retransmit
/// and our reconnect engine should not race with it.
/// </para>
/// </summary>
public static class FixpGapDetector
{
    /// <summary>
    /// Updates <paramref name="last"/> in-place to reflect the highest
    /// in-order seqnum seen and returns what was observed about
    /// <paramref name="incoming"/>.
    /// </summary>
    /// <remarks>
    /// FIXP seqnums are 1-based; a sentinel <c>0</c> means "no message yet".
    /// On <see cref="GapObservation.Gap"/> we still advance <paramref name="last"/>
    /// to <paramref name="incoming"/> so the next message is judged against
    /// the new high-water mark (otherwise every subsequent in-order message
    /// would also be flagged as a gap).
    /// On <see cref="GapObservation.Duplicate"/> we leave <paramref name="last"/>
    /// untouched; the caller can drop the message.
    /// </remarks>
    public static GapObservation Observe(ulong incoming, ref ulong last)
    {
        if (last == 0)
        {
            last = incoming;
            return GapObservation.First;
        }

        var expected = last + 1;
        if (incoming == expected)
        {
            last = incoming;
            return GapObservation.InOrder;
        }
        if (incoming < expected)
        {
            // duplicate or out-of-order replay; do NOT regress last.
            return GapObservation.Duplicate;
        }
        // incoming > expected → gap of (incoming - expected) messages.
        last = incoming;
        return GapObservation.Gap;
    }
}
