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

    /// <summary>
    /// Maximum records per group-commit batch. Raised from 64 → 512 in P5/F7
    /// to amortise fsync over more records at participant-volume throughput
    /// without breaching the <see cref="GroupCommitWindow"/> latency cap.
    /// Worst-case crash exposure (acked-but-unfsynced records) is
    /// <c>ChannelCapacity + GroupCommitMaxRecords</c>; see RFC §4.2 / §5.7.
    /// </summary>
    public int GroupCommitMaxRecords { get; set; } = 512;

    /// <summary>Snapshot cadence.</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>If true, fsync the segment file on every group-commit boundary.</summary>
    public bool FsyncOnFlush { get; set; } = true;

    /// <summary>
    /// One-time migration policy for a non-empty pre-marker WAL. The safe
    /// default rejects an unknown shutdown. Operators may select
    /// <see cref="LegacyWalStartupMode.ControlledCleanShutdown"/> only after
    /// completing the controlled drain/flush/stop procedure in RFC #621.
    /// </summary>
    public LegacyWalStartupMode LegacyWalStartupMode { get; set; } =
        LegacyWalStartupMode.RejectUnknownShutdown;
}

public enum LegacyWalStartupMode
{
    RejectUnknownShutdown = 0,
    ControlledCleanShutdown = 1,
}
