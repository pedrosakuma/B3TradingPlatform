namespace B3.Trading.Application.Audit;

/// <summary>
/// Q4.5 (#305). Tunables for the in-memory <see cref="AuditLogKeeper"/>.
/// Bound from <c>Trading:Audit</c>. The keeper itself is fed by
/// <c>EventReplayer</c> on recovery and by every audit-emitting site
/// at runtime, so the cap bounds both replay-rehydrate cost and
/// steady-state memory — events evicted from the ring buffer are
/// still recoverable from WAL segments via the EOD materialiser.
/// </summary>
public sealed class AuditLogOptions
{
    public const string SectionName = "Trading:Audit";

    /// <summary>
    /// Maximum number of audit entries retained in memory. Older entries
    /// silently drop off the head of the ring buffer once this is hit;
    /// the WAL on disk keeps the full history (subject to segment
    /// retention). Default mirrors the value called out in #305.
    /// </summary>
    public int Capacity { get; set; } = 100_000;
}
