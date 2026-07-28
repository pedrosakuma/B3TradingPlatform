using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Infrastructure.Persistence;

public sealed record ControlledLegacyWalRecoveryRequest(
    string Operator,
    string ChangeTicket,
    string Reason,
    bool ConfirmTailMayBeDiscarded);

public sealed record LegacyWalRecoverySegmentInfo(
    string SegmentId,
    long FirstSeq,
    long LastSeq,
    long EndOffset);

public sealed record LegacyWalRecoveryResult(
    string Status,
    string ReasonCode,
    string Message,
    string WalRoot,
    string MarkerPath,
    Guid? Generation,
    long? LastDurableSeq,
    IReadOnlyList<LegacyWalRecoverySegmentInfo> Segments,
    string? LatestSnapshotPath,
    long? LatestSnapshotSeq,
    string? AuditLogPath);

public sealed class LegacyWalAdministrativeRecovery
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly PersistenceOptions _options;

    public LegacyWalAdministrativeRecovery(PersistenceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public LegacyWalRecoveryResult Inspect()
    {
        var walRoot = ResolveWalRoot(_options);
        var markerPath = Path.Combine(walRoot, FileEventStore.MarkerFileName);

        if (File.Exists(markerPath))
        {
            var marker = WalCommitMarker.Decode(File.ReadAllBytes(markerPath), markerPath);
            return BuildResult(
                status: "no_action_needed",
                reasonCode: "commit_marker_present",
                message: "WAL commit marker already exists; legacy recovery is not needed.",
                walRoot,
                markerPath,
                marker.Generation,
                marker.LastDurableSeq,
                marker.Segments,
                latestSnapshotPath: null,
                latestSnapshotSeq: null,
                auditLogPath: null);
        }

        if (!Directory.Exists(walRoot))
        {
            return new LegacyWalRecoveryResult(
                "no_action_needed",
                "empty_legacy_wal",
                "WAL directory does not exist; startup can initialize a fresh empty marker.",
                walRoot,
                markerPath,
                null,
                0,
                Array.Empty<LegacyWalRecoverySegmentInfo>(),
                null,
                null,
                null);
        }

        var physicalSegments = EnumeratePhysicalSegments(walRoot);
        if (physicalSegments.Count == 0)
        {
            return new LegacyWalRecoveryResult(
                "no_action_needed",
                "empty_legacy_wal",
                "No legacy WAL segments were found; startup can initialize a fresh empty marker.",
                walRoot,
                markerPath,
                null,
                0,
                Array.Empty<LegacyWalRecoverySegmentInfo>(),
                null,
                null,
                null);
        }

        var proposed = BuildProposedLegacyMarker(physicalSegments);
        var latestSnapshot = ReadLatestSnapshotCandidate();
        if (latestSnapshot is { Snapshot: { } snapshot })
        {
            if (snapshot.FormatVersion != 0
                || snapshot.WalGeneration != Guid.Empty
                || snapshot.OutboundLedger is not null)
            {
                throw new LegacyWalAdministrativeRecoveryRefusedException(
                    $"Latest snapshot '{latestSnapshot.Path}' already carries versioned WAL lineage fields; refusing offline legacy-marker recovery.");
            }

            if (snapshot.Seq > proposed.LastDurableSeq)
            {
                throw new LegacyWalAdministrativeRecoveryRefusedException(
                    $"Latest snapshot '{latestSnapshot.Path}' seq={snapshot.Seq} is ahead of the recoverable legacy WAL prefix seq={proposed.LastDurableSeq}; reconcile or delete the uncovered snapshot first.");
            }
        }

        return BuildResult(
            status: "recovery_required",
            reasonCode: "legacy_wal_missing_marker",
            message: "Non-empty legacy WAL has no commit marker. Recovery requires explicit operator confirmation because the tail may include unproven durable data.",
            walRoot,
            markerPath,
            null,
            proposed.LastDurableSeq,
            proposed.Segments,
            latestSnapshot?.Path,
            latestSnapshot?.Snapshot?.Seq,
            null);
    }

    public async Task<LegacyWalRecoveryResult> RecoverAsync(
        ControlledLegacyWalRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.ConfirmTailMayBeDiscarded)
        {
            throw new LegacyWalAdministrativeRecoveryRefusedException(
                "Explicit confirmation is required because this action may discard an ambiguous legacy WAL tail.");
        }

        if (string.IsNullOrWhiteSpace(request.Operator)
            || string.IsNullOrWhiteSpace(request.ChangeTicket)
            || string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException(
                "Operator, change ticket, and reason are required for legacy WAL recovery.",
                nameof(request));
        }

        using var lease = AcquireActiveHostFenceLease();
        var inspection = Inspect();
        if (inspection.Status == "no_action_needed")
            return inspection;

        var auditIntent = CreateAuditIntent(request, inspection);
        var controlledOptions = CloneOptions(_options);
        controlledOptions.LegacyWalStartupMode = LegacyWalStartupMode.ControlledCleanShutdown;
        try
        {
            await using (var store = new FileEventStore(
                             controlledOptions,
                             NullLogger<FileEventStore>.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var marker = WalCommitMarker.Decode(
                File.ReadAllBytes(inspection.MarkerPath),
                inspection.MarkerPath);
            try
            {
                WriteCompletedAuditRecord(
                    auditIntent,
                    request,
                    inspection.WalRoot,
                    marker);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Legacy WAL marker was published, but the audit record could not be finalized. The intent record remains at '{auditIntent.StartedPath}'.",
                    ex);
            }

            return BuildResult(
                status: "recovered",
                reasonCode: "legacy_wal_marker_published",
                message: "Published a commit marker for the legacy WAL using the controlled-clean-shutdown migration path.",
                inspection.WalRoot,
                inspection.MarkerPath,
                marker.Generation,
                marker.LastDurableSeq,
                marker.Segments,
                inspection.LatestSnapshotPath,
                inspection.LatestSnapshotSeq,
                auditIntent.RecoveredPath);
        }
        catch
        {
            if (File.Exists(inspection.MarkerPath))
                TryWritePublishedIncompleteAuditRecord(auditIntent, request, inspection);
            else
                TryWriteFailedAuditRecord(auditIntent, request, inspection);
            throw;
        }
    }

    private LegacyWalRecoveryResult BuildResult(
        string status,
        string reasonCode,
        string message,
        string walRoot,
        string markerPath,
        Guid? generation,
        long? lastDurableSeq,
        IReadOnlyList<WalCommittedSegment> segments,
        string? latestSnapshotPath,
        long? latestSnapshotSeq,
        string? auditLogPath) =>
        new(
            status,
            reasonCode,
            message,
            walRoot,
            markerPath,
            generation,
            lastDurableSeq,
            segments.Select(static segment => new LegacyWalRecoverySegmentInfo(
                segment.SegmentId,
                segment.FirstSeq,
                segment.LastSeq,
                segment.EndOffset)).ToArray(),
            latestSnapshotPath,
            latestSnapshotSeq,
            auditLogPath);

    private SnapshotCandidate? ReadLatestSnapshotCandidate()
    {
        var snapshotRoot = Path.Combine(
            ResolveDeploymentRoot(_options),
            "snapshots");
        if (!Directory.Exists(snapshotRoot))
            return null;

        SnapshotCandidate? latest = null;
        foreach (var path in Directory.EnumerateFiles(snapshotRoot, "snap-*.json"))
        {
            FileEventStore.RejectReparsePoint(path);
            if (!TryParseSnapshotFileName(Path.GetFileName(path), out var seq))
                continue;
            if (latest is null || seq > latest.Seq)
                latest = new SnapshotCandidate(seq, path);
        }

        if (latest is null)
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<PlatformSnapshot>(
                File.ReadAllBytes(latest.Path),
                SnapshotJsonOptions);
            if (snapshot is null || snapshot.Seq != latest.Seq)
            {
                throw new LegacyWalAdministrativeRecoveryRefusedException(
                    $"Latest snapshot '{latest.Path}' could not be matched to its sequence envelope.");
            }
            return latest with { Snapshot = snapshot };
        }
        catch (LegacyWalAdministrativeRecoveryRefusedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new LegacyWalAdministrativeRecoveryRefusedException(
                $"Latest snapshot '{latest.Path}' could not be read; refusing to infer a safe WAL boundary.",
                ex);
        }
    }

    private static bool TryParseSnapshotFileName(string name, out long seq)
    {
        seq = 0;
        const string prefix = "snap-";
        const string suffix = ".json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        var digits = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return digits.Length >= 12
            && digits.Length <= 19
            && digits.IndexOfAnyExceptInRange('0', '9') < 0
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }

    private IReadOnlyDictionary<string, string> EnumeratePhysicalSegments(string walRoot)
    {
        var segments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(walRoot))
        {
            FileEventStore.RejectReparsePoint(entry);
            if (File.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (name is FileEventStore.MarkerFileName or FileEventStore.MarkerStagingFileName)
                    continue;
                throw new WalRecoveryException($"Unexpected artifact in WAL root: '{entry}'.");
            }

            var day = Path.GetFileName(entry);
            if (!DateOnly.TryParseExact(
                    day,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                throw new WalRecoveryException($"Unexpected directory in WAL root: '{entry}'.");
            }

            foreach (var artifact in Directory.EnumerateFileSystemEntries(entry))
            {
                FileEventStore.RejectReparsePoint(artifact);
                var name = Path.GetFileName(artifact);
                if (Directory.Exists(artifact))
                    throw new WalRecoveryException($"Unexpected nested WAL directory: '{artifact}'.");
                if (name.EndsWith(".log", StringComparison.Ordinal))
                {
                    var segmentId = day + "/" + name;
                    FileEventStore.ValidateSegmentId(segmentId);
                    segments.Add(segmentId, artifact);
                    continue;
                }

                if (!TryGetCompanionLogFileName(name, out _))
                    throw new WalRecoveryException($"Unexpected WAL segment artifact: '{artifact}'.");
            }
        }

        return segments;
    }

    private static bool TryGetCompanionLogFileName(
        string artifactName,
        out string logFileName)
    {
        if (artifactName.EndsWith(
                ".log.firstseq" + FileEventStore.MigrationMetadataSuffix,
                StringComparison.Ordinal))
        {
            logFileName = artifactName[
                ..^(".firstseq" + FileEventStore.MigrationMetadataSuffix).Length];
            return true;
        }
        if (artifactName.EndsWith(".log.firstseq.tmp", StringComparison.Ordinal))
        {
            logFileName = artifactName[..^".firstseq.tmp".Length];
            return true;
        }
        if (artifactName.EndsWith(".log.firstseq", StringComparison.Ordinal))
        {
            logFileName = artifactName[..^".firstseq".Length];
            return true;
        }
        if (artifactName.EndsWith(".idx", StringComparison.Ordinal))
        {
            logFileName = artifactName[..^".idx".Length] + ".log";
            return true;
        }

        logFileName = "";
        return false;
    }

    private static WalCommitMarker BuildProposedLegacyMarker(
        IReadOnlyDictionary<string, string> physicalSegments)
    {
        var legacy = new List<LegacySegment>();
        foreach (var (segmentId, path) in physicalSegments)
        {
            var sidecar = path + SegmentWriter.FirstSeqSidecarSuffix;
            long? hintedFirstSeq = null;
            if (File.Exists(sidecar))
            {
                var bytes = File.ReadAllBytes(sidecar);
                if (bytes.Length != 8)
                {
                    throw new WalRecoveryException(
                        $"Legacy WAL sidecar '{sidecar}' is corrupt or already generation-bound without a marker.");
                }
                hintedFirstSeq = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes);
                if (hintedFirstSeq <= 0)
                    throw new WalRecoveryException($"Legacy WAL sidecar '{sidecar}' has an invalid sequence.");
            }

            legacy.Add(new LegacySegment(segmentId, path, hintedFirstSeq));
        }

        legacy.Sort(LegacySegment.OrderingComparer);
        var manifest = new List<WalCommittedSegment>();
        long nextSeq = 1;
        foreach (var segment in legacy)
        {
            var length = new FileInfo(segment.Path).Length;
            SegmentReader.SegmentScanResult scan;
            using (var reader = new SegmentReader(segment.Path))
                scan = reader.ScanThrough(length);
            if (!scan.IsValid)
            {
                throw new WalRecoveryException(
                    $"Controlled legacy WAL migration found corruption in '{segment.Path}' at offset {scan.LastValidEnd}: {scan.Failure}.");
            }
            if (scan.RecordCount == 0)
                continue;
            if (segment.HintedFirstSeq is long hinted && hinted != nextSeq)
            {
                throw new WalRecoveryException(
                    $"Legacy WAL sequence sidecar for '{segment.Path}' expected {nextSeq} but contained {hinted}.");
            }

            var lastSeq = checked(nextSeq + scan.RecordCount - 1);
            manifest.Add(new WalCommittedSegment(
                segment.SegmentId,
                nextSeq,
                lastSeq,
                length));
            nextSeq = checked(lastSeq + 1);
        }

        return new WalCommitMarker(Guid.NewGuid(), nextSeq - 1, manifest);
    }

    private ActiveHostFenceLease AcquireActiveHostFenceLease()
    {
        var deploymentRoot = ResolveDeploymentRoot(_options);
        var fenceRoot = Path.Combine(deploymentRoot, "active-host");
        Directory.CreateDirectory(fenceRoot);
        var fencePaths = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(fenceRoot, "maintenance.lock"),
        };
        foreach (var existingLock in Directory.EnumerateFiles(fenceRoot, "*.lock"))
            fencePaths.Add(existingLock);

        var leases = new List<FileStream>(fencePaths.Count);
        try
        {
            foreach (var fencePath in fencePaths.OrderBy(static path => path, StringComparer.Ordinal))
            {
                leases.Add(new FileStream(
                    fencePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough));
            }
            return new ActiveHostFenceLease(leases);
        }
        catch (IOException ex)
        {
            foreach (var lease in leases)
                lease.Dispose();
            throw new LegacyWalAdministrativeRecoveryRefusedException(
                $"Refusing offline WAL recovery while another process holds an active-host fence under '{fenceRoot}'. Scale the writer down first.",
                ex);
        }
    }

    private AuditIntent CreateAuditIntent(
        ControlledLegacyWalRecoveryRequest request,
        LegacyWalRecoveryResult inspection)
    {
        var maintenanceRoot = Path.Combine(
            ResolveDeploymentRoot(_options),
            "maintenance",
            "legacy-wal-recovery");
        Directory.CreateDirectory(maintenanceRoot);
        var prefix = Path.Combine(
            maintenanceRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var startedPath = prefix + ".started.json";
        WriteAuditRecord(
            startedPath,
            new
            {
                status = "started",
                requestedAtUtc = DateTimeOffset.UtcNow,
                request.Operator,
                request.ChangeTicket,
                request.Reason,
                inspection.WalRoot,
                inspection.MarkerPath,
                inspection.LastDurableSeq,
                inspection.Segments,
                inspection.LatestSnapshotPath,
                inspection.LatestSnapshotSeq,
            });
        return new AuditIntent(
            startedPath,
            prefix + ".recovered.json",
            prefix + ".published-incomplete.json",
            prefix + ".failed.json");
    }

    private void WriteCompletedAuditRecord(
        AuditIntent auditIntent,
        ControlledLegacyWalRecoveryRequest request,
        string walRoot,
        WalCommitMarker marker) =>
        WriteAuditRecord(
            auditIntent.RecoveredPath,
            new
            {
                status = "recovered",
                recoveredAtUtc = DateTimeOffset.UtcNow,
                request.Operator,
                request.ChangeTicket,
                request.Reason,
                walRoot,
                marker.Generation,
                marker.LastDurableSeq,
                segments = marker.Segments.Select(static segment => new
                {
                    segment.SegmentId,
                    segment.FirstSeq,
                    segment.LastSeq,
                    segment.EndOffset,
                }).ToArray(),
            });

    private void TryWriteFailedAuditRecord(
        AuditIntent auditIntent,
        ControlledLegacyWalRecoveryRequest request,
        LegacyWalRecoveryResult inspection)
    {
        try
        {
            WriteAuditRecord(
                auditIntent.FailedPath,
                new
                {
                    status = "failed",
                    failedAtUtc = DateTimeOffset.UtcNow,
                    request.Operator,
                    request.ChangeTicket,
                    request.Reason,
                    inspection.WalRoot,
                    inspection.MarkerPath,
                    inspection.LastDurableSeq,
                    inspection.Segments,
                    inspection.LatestSnapshotPath,
                    inspection.LatestSnapshotSeq,
                });
        }
        catch
        {
        }
    }

    private void TryWritePublishedIncompleteAuditRecord(
        AuditIntent auditIntent,
        ControlledLegacyWalRecoveryRequest request,
        LegacyWalRecoveryResult inspection)
    {
        try
        {
            var marker = WalCommitMarker.Decode(
                File.ReadAllBytes(inspection.MarkerPath),
                inspection.MarkerPath);
            WriteAuditRecord(
                auditIntent.PublishedIncompletePath,
                new
                {
                    status = "marker_published_incomplete",
                    recordedAtUtc = DateTimeOffset.UtcNow,
                    request.Operator,
                    request.ChangeTicket,
                    request.Reason,
                    inspection.WalRoot,
                    inspection.MarkerPath,
                    marker.Generation,
                    marker.LastDurableSeq,
                    segments = marker.Segments.Select(static segment => new
                    {
                        segment.SegmentId,
                        segment.FirstSeq,
                        segment.LastSeq,
                        segment.EndOffset,
                    }).ToArray(),
                });
        }
        catch
        {
        }
    }

    private static void WriteAuditRecord(string auditLogPath, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var stagingPath = auditLogPath + ".writing";
        using (var stream = new FileStream(
                   stagingPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(stagingPath, auditLogPath, overwrite: true);
        ReconciliationDirectoryDurability.Instance.Flush(
            Path.GetDirectoryName(auditLogPath)
            ?? throw new IOException($"Audit record '{auditLogPath}' has no parent directory."));
    }

    private static PersistenceOptions CloneOptions(PersistenceOptions options) =>
        new()
        {
            Enabled = options.Enabled,
            DataDirectory = options.DataDirectory,
            FirmId = options.FirmId,
            SegmentMaxBytes = options.SegmentMaxBytes,
            IndexEveryNRecords = options.IndexEveryNRecords,
            IndexEveryNBytes = options.IndexEveryNBytes,
            ChannelCapacity = options.ChannelCapacity,
            GroupCommitWindow = options.GroupCommitWindow,
            GroupCommitMaxRecords = options.GroupCommitMaxRecords,
            SnapshotInterval = options.SnapshotInterval,
            FsyncOnFlush = options.FsyncOnFlush,
            LegacyWalStartupMode = options.LegacyWalStartupMode,
        };

    private static string ResolveWalRoot(PersistenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FirmId)
            || Path.IsPathRooted(options.FirmId)
            || options.FirmId is "." or ".."
            || options.FirmId.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("Persistence FirmId must be a relative non-empty path segment.");
        }

        var dataRoot = Path.GetFullPath(options.DataDirectory);
        var firmRoot = Path.GetFullPath(Path.Combine(dataRoot, options.FirmId));
        var relative = Path.GetRelativePath(dataRoot, firmRoot);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Persistence FirmId escapes DataDirectory.");
        }

        return Path.Combine(firmRoot, "wal");
    }

    private static string ResolveDeploymentRoot(PersistenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FirmId)
            || Path.IsPathRooted(options.FirmId)
            || options.FirmId is "." or ".."
            || options.FirmId.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException("Persistence FirmId must be a relative non-empty path segment.");
        }

        var dataRoot = Path.GetFullPath(options.DataDirectory);
        var deploymentRoot = Path.GetFullPath(Path.Combine(dataRoot, options.FirmId));
        if (!deploymentRoot.StartsWith(
                dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persistence FirmId escapes DataDirectory.");
        }

        Directory.CreateDirectory(deploymentRoot);
        return deploymentRoot;
    }

    private sealed record SnapshotCandidate(
        long Seq,
        string Path,
        PlatformSnapshot? Snapshot = null);

    private sealed record AuditIntent(
        string StartedPath,
        string RecoveredPath,
        string PublishedIncompletePath,
        string FailedPath);

    private sealed class ActiveHostFenceLease : IDisposable
    {
        private readonly IReadOnlyList<FileStream> _leases;

        public ActiveHostFenceLease(IReadOnlyList<FileStream> leases) => _leases = leases;

        public void Dispose()
        {
            foreach (var lease in _leases)
                lease.Dispose();
        }
    }

    private readonly record struct LegacySegment(
        string SegmentId,
        string Path,
        long? HintedFirstSeq)
    {
        public static IComparer<LegacySegment> OrderingComparer { get; } =
            Comparer<LegacySegment>.Create(static (a, b) =>
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

public sealed class LegacyWalAdministrativeRecoveryRefusedException : IOException
{
    public LegacyWalAdministrativeRecoveryRefusedException(string message)
        : base(message) { }

    public LegacyWalAdministrativeRecoveryRefusedException(
        string message,
        Exception innerException)
        : base(message, innerException) { }
}
