namespace B3.Trading.Application.Persistence;

/// <summary>
/// End-of-day report produced by <see cref="IEodMaterialiser.Materialise"/>.
/// Self-describing JSON shape: every field is included even when zero so the
/// EOD reconciliation tooling can <c>diff</c> two days without missing-key
/// surprises.
/// </summary>
public sealed class EodReport
{
    public string Date { get; set; } = "";
    public string FirmId { get; set; } = "";
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public long RecordCount { get; set; }
    public long OrderSubmittedCount { get; set; }
    public long ExecutionReportCount { get; set; }
    public long FilledCount { get; set; }
    public long PartialFillCount { get; set; }
    public long CanceledCount { get; set; }
    public long RejectedCount { get; set; }
    public long KillSwitchToggleCount { get; set; }
    public long SymbolHaltToggleCount { get; set; }
    public long SessionPhaseChangeCount { get; set; }
    public string Sha256 { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>
/// Application-layer port for materialising the day-segmented WAL into a
/// single self-describing EOD JSON summary. Decouples the Api layer from
/// the Infrastructure-owned <c>EodMaterialiser</c> concretion (which also
/// owns the segment-reader plumbing).
///
/// <para>
/// <see cref="IsAvailable"/> reflects whether persistence is enabled at all.
/// When <c>false</c>, the admin endpoint surfaces 409 instead of silently
/// producing an empty report.
/// </para>
/// </summary>
public interface IEodMaterialiser
{
    /// <summary>True when persistence is enabled and the segments directory
    /// is reachable; false when persistence is disabled (no-op store).</summary>
    bool IsAvailable { get; }

    /// <summary>Materialise the EOD summary for <paramref name="date"/>.
    /// Implementations should throw <see cref="InvalidOperationException"/>
    /// when called while <see cref="IsAvailable"/> is <c>false</c>.</summary>
    EodReport Materialise(DateOnly date);
}
