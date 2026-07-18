using System.Globalization;
using System.Text.Json;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Fsync-backed atomic store for versioned platform snapshots. Snapshot files
/// are authoritative candidates; <c>latest.txt</c> is only a repairable hint.
/// </summary>
public sealed class SnapshotStore
{
    private const string LatestFileName = "latest.txt";
    private const string WritingSuffix = ".writing";
    private const string LegacyTempSuffix = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly object _lock = new();
    private readonly IReconciliationDirectoryDurability _directoryDurability;

    public SnapshotStore(string dataDirectory, string firmId)
        : this(dataDirectory, firmId, ReconciliationDirectoryDurability.Instance)
    {
    }

    internal SnapshotStore(
        string dataDirectory,
        string firmId,
        IReconciliationDirectoryDurability directoryDurability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        _directoryDurability = directoryDurability
            ?? throw new ArgumentNullException(nameof(directoryDurability));

        var dataRoot = Path.GetFullPath(dataDirectory);
        _root = Path.GetFullPath(Path.Combine(dataRoot, firmId, "snapshots"));
        var relative = Path.GetRelativePath(dataRoot, _root);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Persistence FirmId escapes DataDirectory.", nameof(firmId));
        }

        Directory.CreateDirectory(_root);
        RejectReparsePoints(dataRoot, _root);
    }

    public string Root => _root;

    public void Write(PlatformSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Seq < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Snapshot sequence must be non-negative.");

        lock (_lock)
        {
            RejectReparsePoint(_root);
            var name = SnapshotFileName(snapshot.Seq);
            var path = Path.Combine(_root, name);
            var staging = path + WritingSuffix;
            WriteAndFlush(staging, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
            _directoryDurability.Flush(_root);
            File.Move(staging, path, overwrite: true);
            _directoryDurability.Flush(_root);

            var pointer = Path.Combine(_root, LatestFileName);
            var pointerStaging = pointer + WritingSuffix;
            WriteAndFlush(
                pointerStaging,
                System.Text.Encoding.UTF8.GetBytes(
                    snapshot.Seq.ToString(CultureInfo.InvariantCulture)));
            _directoryDurability.Flush(_root);
            File.Move(pointerStaging, pointer, overwrite: true);
            _directoryDurability.Flush(_root);
        }
    }

    public PlatformSnapshot? LoadLatest() =>
        LoadLatest(
            static _ => SnapshotValidationResult.Accept(),
            rejected: null);

    internal PlatformSnapshot? LoadLatest(
        Func<PlatformSnapshot, SnapshotValidationResult> validate,
        Action<string>? rejected)
    {
        ArgumentNullException.ThrowIfNull(validate);
        lock (_lock)
        {
            RejectReparsePoint(_root);
            CleanupStagingArtifacts();
            var candidates = EnumerateCandidates();
            foreach (var candidate in candidates)
            {
                PlatformSnapshot? snapshot;
                try
                {
                    snapshot = JsonSerializer.Deserialize<PlatformSnapshot>(
                        File.ReadAllBytes(candidate.Path),
                        JsonOptions);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    rejected?.Invoke(
                        $"Snapshot '{candidate.Path}' could not be read and was skipped: {ex.Message}");
                    continue;
                }

                if (snapshot is null || snapshot.Seq != candidate.Seq)
                {
                    rejected?.Invoke(
                        $"Snapshot '{candidate.Path}' does not match its filename sequence and was skipped.");
                    continue;
                }

                var validation = validate(snapshot);
                if (validation.IsFatal)
                    throw new SnapshotRecoveryException(validation.Reason);
                if (!validation.IsAccepted)
                {
                    rejected?.Invoke(
                        $"Snapshot '{candidate.Path}' was rejected: {validation.Reason}");
                    continue;
                }
                return snapshot;
            }
            return null;
        }
    }

    private List<SnapshotCandidate> EnumerateCandidates()
    {
        var candidates = new List<SnapshotCandidate>();
        foreach (var path in Directory.EnumerateFileSystemEntries(_root))
        {
            RejectReparsePoint(path);
            if (Directory.Exists(path))
                throw new SnapshotRecoveryException(
                    $"Unexpected directory in snapshot store: '{path}'.");

            var name = Path.GetFileName(path);
            if (name is LatestFileName)
                continue;
            if (name == LatestFileName + WritingSuffix
                || name.EndsWith(WritingSuffix, StringComparison.Ordinal)
                || name.EndsWith(LegacyTempSuffix, StringComparison.Ordinal))
            {
                throw new SnapshotRecoveryException(
                    $"Snapshot staging artifact survived cleanup: '{path}'.");
            }
            if (!TryParseSnapshotFileName(name, out var seq))
                throw new SnapshotRecoveryException(
                    $"Unexpected snapshot artifact: '{path}'.");
            if (!string.Equals(name, SnapshotFileName(seq), StringComparison.Ordinal))
                throw new SnapshotRecoveryException(
                    $"Snapshot artifact has a non-canonical sequence name: '{path}'.");
            candidates.Add(new SnapshotCandidate(seq, path));
        }
        candidates.Sort(static (left, right) => right.Seq.CompareTo(left.Seq));
        return candidates;
    }

    private void CleanupStagingArtifacts()
    {
        var changed = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(_root))
        {
            RejectReparsePoint(path);
            if (Directory.Exists(path))
                continue;

            var name = Path.GetFileName(path);
            if (name == LatestFileName + WritingSuffix)
            {
                File.Delete(path);
                changed = true;
                continue;
            }

            if (name.EndsWith(WritingSuffix, StringComparison.Ordinal))
            {
                var finalName = name[..^WritingSuffix.Length];
                if (!TryParseSnapshotFileName(finalName, out var seq)
                    || !string.Equals(
                        finalName,
                        SnapshotFileName(seq),
                        StringComparison.Ordinal))
                {
                    continue;
                }
                File.Delete(path);
                changed = true;
                continue;
            }

            if (name.EndsWith(LegacyTempSuffix, StringComparison.Ordinal))
            {
                var finalName = name[..^LegacyTempSuffix.Length];
                if (!TryParseSnapshotFileName(finalName, out var seq)
                    || !string.Equals(
                        finalName,
                        SnapshotFileName(seq),
                        StringComparison.Ordinal))
                {
                    continue;
                }
                File.Delete(path);
                changed = true;
            }
        }
        if (changed)
            _directoryDurability.Flush(_root);
    }

    private static string SnapshotFileName(long seq) =>
        $"snap-{seq.ToString("D12", CultureInfo.InvariantCulture)}.json";

    private static bool TryParseSnapshotFileName(string name, out long seq)
    {
        seq = 0;
        const string prefix = "snap-";
        const string suffix = ".json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var digits = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return digits.Length >= 12
            && digits.Length <= 19
            && digits.IndexOfAnyExceptInRange('0', '9') < 0
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }

    private static void WriteAndFlush(string path, byte[] payload)
    {
        PrepareStagingPath(path);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private static void RejectReparsePoints(string dataRoot, string snapshotRoot)
    {
        var current = dataRoot;
        while (true)
        {
            RejectReparsePoint(current);
            if (string.Equals(current, snapshotRoot, StringComparison.Ordinal))
                break;
            var relative = Path.GetRelativePath(current, snapshotRoot);
            current = Path.Combine(
                current,
                relative.Split(Path.DirectorySeparatorChar)[0]);
        }
    }

    private static void PrepareStagingPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SnapshotRecoveryException(
                    $"Snapshot staging path must not be a symbolic link or reparse point: '{path}'.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new SnapshotRecoveryException(
                    $"Snapshot staging path must not be a directory: '{path}'.");
            }
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new SnapshotRecoveryException(
                $"Snapshot path must not be a symbolic link or reparse point: '{path}'.");
        }
    }

    private readonly record struct SnapshotCandidate(long Seq, string Path);
}

internal readonly record struct SnapshotValidationResult(
    bool IsAccepted,
    bool IsFatal,
    string Reason)
{
    public static SnapshotValidationResult Accept() => new(true, false, string.Empty);

    public static SnapshotValidationResult Reject(string reason) => new(false, false, reason);

    public static SnapshotValidationResult Fatal(string reason) => new(false, true, reason);
}

public sealed class SnapshotRecoveryException : IOException
{
    public SnapshotRecoveryException(string message) : base(message)
    {
    }
}
