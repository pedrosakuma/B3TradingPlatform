namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Strongly-typed binding for the <c>Trading:Persistence</c> configuration
/// section. Defaults are tuned for participant-side volumes (single-firm,
/// ≤30k events/day); revisit when measured pressure changes.
///
/// <para>
/// When <see cref="Enabled"/> is <c>false</c>, the host wires
/// <c>NullEventStore</c> and skips snapshot + recovery entirely. Used by
/// integration tests and ephemeral demos that don't want a data dir.
/// </para>
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Trading:Persistence";

    public bool Enabled { get; set; } = true;

    /// <summary>Root data directory; one subdir per firm.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Firm id slug used as the per-firm subdirectory name.</summary>
    public string FirmId { get; set; } = "default";

    /// <summary>Roll a new segment file once the active one passes this size.</summary>
    public long SegmentMaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Add an index entry at most every N records OR every M bytes, whichever first.</summary>
    public int IndexEveryNRecords { get; set; } = 64;
    public int IndexEveryNBytes { get; set; } = 4096;

    /// <summary>Bounded write-behind channel capacity. Full → <c>WalBackpressureException</c>.</summary>
    public int ChannelCapacity { get; set; } = 4096;

    /// <summary>Group-commit window: writer flushes after either limit.</summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.FromMilliseconds(10);
    public int GroupCommitMaxRecords { get; set; } = 64;

    /// <summary>Snapshot cadence.</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>If true, fsync the segment file on every group-commit boundary.</summary>
    public bool FsyncOnFlush { get; set; } = true;
}
