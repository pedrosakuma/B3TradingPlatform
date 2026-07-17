using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Materialises the day-segmented WAL into a single self-describing
/// <c>eod-{date}.json</c> summary used for end-of-day reconciliation.
/// Reads the day's segments with a fresh <see cref="SegmentReader"/>
/// (independent of the live writer) so it can run while the platform
/// keeps trading the next session.
///
/// <para>
/// Comparison against an exchange-side EOD report is intentionally a
/// future hook — the current EntryPoint stub does not expose one. The
/// summary itself is enough for self-audit ("what did we send/receive
/// today?") and for diff'ing against a manually-supplied EP report.
/// </para>
/// </summary>
public sealed class EodMaterialiser : IEodMaterialiser
{
    public bool IsAvailable => true;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly PersistenceOptions _opts;

    public EodMaterialiser(IOptions<PersistenceOptions> opts) : this(opts.Value) { }
    public EodMaterialiser(PersistenceOptions opts) => _opts = opts;

    public EodReport Materialise(DateOnly date)
    {
        var walRoot = Path.GetFullPath(Path.Combine(
            _opts.DataDirectory, _opts.FirmId, "wal"));
        var dayDir = Path.Combine(walRoot,
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var report = new EodReport
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FirmId = _opts.FirmId,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };

        var payloads = ReadReportPayloads(walRoot, date);
        if (!Directory.Exists(dayDir) && payloads.Count == 0) return report;

        using var sha = SHA256.Create();
        foreach (var payload in payloads)
        {
            report.RecordCount++;
            sha.TransformBlock(payload, 0, payload.Length, null, 0);
            if (FileEventStore.TryDeserialize(payload, out var evt, out _) != FileEventStore.DeserializeOutcome.Ok)
            {
                // Unknown future kinds remain countable, but structural
                // frame/marker corruption has already failed closed above.
                continue;
            }
            switch (evt)
            {
                case OrderSubmittedEvent: report.OrderSubmittedCount++; break;
                case ExecutionReportReceivedEvent er:
                    report.ExecutionReportCount++;
                    if (er.ExecKind.Equals("Fill", StringComparison.OrdinalIgnoreCase))
                        report.FilledCount++;
                    else if (er.ExecKind.Equals("PartialFill", StringComparison.OrdinalIgnoreCase))
                        report.PartialFillCount++;
                    else if (er.ExecKind.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
                        report.CanceledCount++;
                    else if (er.ExecKind.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                        report.RejectedCount++;
                    break;
                case KillSwitchToggledEvent: report.KillSwitchToggleCount++; break;
                case SymbolHaltToggledEvent: report.SymbolHaltToggleCount++; break;
                case SessionPhaseChangedEvent: report.SessionPhaseChangeCount++; break;
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        report.Sha256 = Convert.ToHexString(sha.Hash!);

        var eodDir = Path.Combine(_opts.DataDirectory, _opts.FirmId, "eod");
        Directory.CreateDirectory(eodDir);
        var path = Path.Combine(eodDir, $"eod-{report.Date}.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
        report.Path = path;
        return report;
    }

    private IReadOnlyList<byte[]> ReadReportPayloads(string walRoot, DateOnly date)
    {
        if (!Directory.Exists(walRoot))
            return Array.Empty<byte[]>();

        var markerPath = Path.Combine(walRoot, FileEventStore.MarkerFileName);
        if (File.Exists(markerPath))
            return ReadCommittedPayloads(walRoot, markerPath, date);

        var legacyLogs = Directory.EnumerateFiles(
            walRoot, "*.log", SearchOption.AllDirectories).ToArray();
        if (legacyLogs.Length == 0)
            return Array.Empty<byte[]>();
        if (_opts.LegacyWalStartupMode != LegacyWalStartupMode.ControlledCleanShutdown)
        {
            throw new WalLegacyMigrationRequiredException(
                "EOD materialisation refused a non-empty WAL without commit.marker after an unknown shutdown.");
        }
        return ReadControlledLegacyPayloads(walRoot, legacyLogs, date);
    }

    private static IReadOnlyList<byte[]> ReadCommittedPayloads(
        string walRoot,
        string markerPath,
        DateOnly date)
    {
        WalCommitMarker marker;
        try
        {
            FileEventStore.RejectReparsePoint(markerPath);
            marker = WalCommitMarker.Decode(File.ReadAllBytes(markerPath), markerPath);
        }
        catch (WalRecoveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WalRecoveryException(
                $"EOD materialisation could not read commit marker '{markerPath}'.", ex);
        }

        var selectedDay = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var selected = new List<byte[]>();
        foreach (var segment in marker.Segments)
        {
            var path = FileEventStore.ResolveSegmentPath(
                walRoot, segment.SegmentId);
            if (!File.Exists(path))
                throw new WalRecoveryException(
                    $"EOD materialisation is missing committed segment '{segment.SegmentId}'.");
            FileEventStore.RejectReparsePoint(path);

            var metadataPath = path + SegmentWriter.FirstSeqSidecarSuffix;
            if (!File.Exists(metadataPath))
                throw new WalRecoveryException(
                    $"EOD materialisation is missing metadata for committed segment '{segment.SegmentId}'.");
            FileEventStore.RejectReparsePoint(metadataPath);
            var metadata = SegmentMetadata.Decode(
                File.ReadAllBytes(metadataPath), metadataPath);
            if (metadata.Generation != marker.Generation
                || metadata.FirstSeq != segment.FirstSeq)
            {
                throw new WalRecoveryException(
                    $"EOD materialisation found generation/sequence mismatch for '{segment.SegmentId}'.");
            }

            using var reader = new SegmentReader(path);
            var records = reader.ReadAllThrough(segment.EndOffset);
            var expectedCount = checked(segment.LastSeq - segment.FirstSeq + 1);
            if (records.Count != expectedCount)
                throw new WalRecoveryException(
                    $"EOD materialisation found record-count mismatch for '{segment.SegmentId}'.");
            if (segment.SegmentId.StartsWith(
                    selectedDay + "/", StringComparison.Ordinal))
            {
                selected.AddRange(records);
            }
        }
        return selected;
    }

    private static IReadOnlyList<byte[]> ReadControlledLegacyPayloads(
        string walRoot,
        IEnumerable<string> logPaths,
        DateOnly date)
    {
        var segments = new List<LegacyEodSegment>();
        foreach (var path in logPaths)
        {
            var fullPath = Path.GetFullPath(path);
            FileEventStore.RejectReparsePoint(fullPath);
            var relative = Path.GetRelativePath(walRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            FileEventStore.ValidateSegmentId(relative);

            long? hintedFirstSeq = null;
            var sidecar = fullPath + SegmentWriter.FirstSeqSidecarSuffix;
            if (File.Exists(sidecar))
            {
                FileEventStore.RejectReparsePoint(sidecar);
                var bytes = File.ReadAllBytes(sidecar);
                if (bytes.Length != 8)
                    throw new WalRecoveryException(
                        $"Controlled legacy EOD sidecar '{sidecar}' is not legacy format.");
                hintedFirstSeq = System.Buffers.Binary.BinaryPrimitives
                    .ReadInt64LittleEndian(bytes);
                if (hintedFirstSeq <= 0)
                    throw new WalRecoveryException(
                        $"Controlled legacy EOD sidecar '{sidecar}' has invalid sequence.");
            }
            segments.Add(new LegacyEodSegment(relative, fullPath, hintedFirstSeq));
        }

        segments.Sort(LegacyEodSegment.OrderingComparer);
        var selectedDay = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var selected = new List<byte[]>();
        long nextSeq = 1;
        foreach (var segment in segments)
        {
            using var reader = new SegmentReader(segment.Path);
            var records = reader.ReadAllThrough(new FileInfo(segment.Path).Length);
            if (records.Count == 0)
                continue;
            if (segment.HintedFirstSeq is long hinted && hinted != nextSeq)
                throw new WalRecoveryException(
                    $"Controlled legacy EOD expected sequence {nextSeq} for '{segment.SegmentId}', found {hinted}.");
            nextSeq = checked(nextSeq + records.Count);
            if (segment.SegmentId.StartsWith(
                    selectedDay + "/", StringComparison.Ordinal))
            {
                selected.AddRange(records);
            }
        }
        return selected;
    }

    private readonly record struct LegacyEodSegment(
        string SegmentId,
        string Path,
        long? HintedFirstSeq)
    {
        public static IComparer<LegacyEodSegment> OrderingComparer { get; } =
            Comparer<LegacyEodSegment>.Create(static (a, b) =>
            {
                if (a.HintedFirstSeq.HasValue && b.HintedFirstSeq.HasValue)
                    return a.HintedFirstSeq.Value.CompareTo(b.HintedFirstSeq.Value);
                if (a.HintedFirstSeq.HasValue)
                    return 1;
                if (b.HintedFirstSeq.HasValue)
                    return -1;
                return StringComparer.Ordinal.Compare(a.SegmentId, b.SegmentId);
            });
    }
}

/// <summary>
/// Drop-in <see cref="IEodMaterialiser"/> registered when persistence is
/// disabled. <see cref="IsAvailable"/> is <c>false</c>, and
/// <see cref="Materialise"/> throws so any caller that bypasses the
/// availability check fails loudly instead of producing an empty report.
/// </summary>
public sealed class DisabledEodMaterialiser : IEodMaterialiser
{
    public bool IsAvailable => false;

    public EodReport Materialise(DateOnly date) =>
        throw new InvalidOperationException(
            "EOD materialisation is unavailable: persistence is disabled.");
}
