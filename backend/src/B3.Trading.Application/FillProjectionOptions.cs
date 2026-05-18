namespace B3.Trading.Application;

/// <summary>
/// Q4.7 (#307). Tunables for the in-memory <see cref="FillProjection"/>.
/// Bound from <c>Trading:FillProjection</c>. The projection is rehydrated
/// from the WAL on recovery (audit-style pre-pass + post-snapshot tail),
/// so the cap bounds both replay-rehydrate cost and steady-state memory.
/// Older fills evicted from the in-memory dictionary remain durable on
/// disk via WAL segment retention and can be reconstructed from there.
/// </summary>
public sealed class FillProjectionOptions
{
    public const string SectionName = "Trading:FillProjection";

    /// <summary>
    /// Maximum number of fill records retained in memory. When the cap
    /// is hit, the oldest insertion is evicted (FIFO) before the new
    /// record is admitted. Default sized for a busy single-firm trading
    /// day (~1M fills); operators with higher throughput or longer
    /// retention windows should override via configuration.
    /// </summary>
    public int Capacity { get; set; } = 1_000_000;
}
