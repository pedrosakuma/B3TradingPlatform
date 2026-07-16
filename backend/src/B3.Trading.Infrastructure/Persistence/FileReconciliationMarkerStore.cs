using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Fsync-backed sidecar store for outbound reconciliation markers. Markers
/// live outside the WAL so a terminal WAL fault cannot erase the fail-closed
/// startup signal.
/// </summary>
public sealed class FileReconciliationMarkerStore : IReconciliationMarkerStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly object _lock = new();
    private readonly IReconciliationDirectoryDurability _directoryDurability;
    private readonly IReconciliationMarkerFileOperations _fileOperations;

    public FileReconciliationMarkerStore(IOptions<PersistenceOptions> options)
        : this(options.Value)
    {
    }

    public FileReconciliationMarkerStore(PersistenceOptions options)
        : this(
            options,
            ReconciliationDirectoryDurability.Instance,
            ReconciliationMarkerFileOperations.Instance)
    {
    }

    public FileReconciliationMarkerStore(
        PersistenceOptions options,
        IReconciliationDirectoryDurability directoryDurability)
        : this(
            options,
            directoryDurability,
            ReconciliationMarkerFileOperations.Instance)
    {
    }

    public FileReconciliationMarkerStore(
        PersistenceOptions options,
        IReconciliationDirectoryDurability directoryDurability,
        IReconciliationMarkerFileOperations fileOperations)
    {
        _directoryDurability = directoryDurability
            ?? throw new ArgumentNullException(nameof(directoryDurability));
        _fileOperations = fileOperations
            ?? throw new ArgumentNullException(nameof(fileOperations));
        _root = Path.GetFullPath(Path.Combine(
            options.DataDirectory, options.FirmId, "reconciliation"));
        CreateDirectoryPathDurably(_root);
    }

    public void Persist(ReconciliationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        lock (_lock)
        {
            var path = PathFor(marker.Id);
            var staging = path + ".writing";
            var durablyPublished = false;
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions);
                _fileOperations.WriteAndFlush(staging, payload);
                // Publish the staging entry durably before rename. A crash at
                // this boundary leaves a recoverable .json.writing marker.
                _directoryDurability.Flush(_root);
                durablyPublished = true;
                _fileOperations.Move(staging, path);
                _directoryDurability.Flush(_root);
            }
            catch (Exception ex)
            {
                throw new ReconciliationMarkerPersistException(
                    $"Failed to durably publish reconciliation marker '{marker.Id}'.",
                    durablyPublished,
                    ex);
            }
        }
    }

    public void Remove(string markerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerId);
        lock (_lock)
        {
            var path = PathFor(markerId);
            var staging = path + ".writing";
            var deleted = false;
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }
            if (File.Exists(staging))
            {
                File.Delete(staging);
                deleted = true;
            }
            if (deleted)
                _directoryDurability.Flush(_root);
        }
    }

    public IReadOnlyList<ReconciliationMarker> Load()
    {
        lock (_lock)
        {
            var artifacts = new Dictionary<string, MarkerArtifacts>(
                StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFileSystemEntries(_root))
            {
                if (Directory.Exists(path))
                    throw new InvalidDataException(
                        $"Unexpected directory in reconciliation store: '{path}'.");

                var name = Path.GetFileName(path);
                var isStaging = name.EndsWith(
                    ".json.writing", StringComparison.Ordinal);
                var isFinal = !isStaging
                    && name.EndsWith(".json", StringComparison.Ordinal);
                if (!isStaging && !isFinal)
                    throw new InvalidDataException(
                        $"Unexpected reconciliation artifact: '{path}'.");

                var markerId = isStaging
                    ? name[..^".json.writing".Length]
                    : name[..^".json".Length];
                _ = PathFor(markerId);
                var marker = ReadMarker(path, markerId);
                ref var entry = ref System.Runtime.InteropServices
                    .CollectionsMarshal.GetValueRefOrAddDefault(
                        artifacts, markerId, out _);
                entry ??= new MarkerArtifacts();
                if (isStaging)
                {
                    if (entry.Staging is not null)
                        throw new InvalidDataException(
                            $"Duplicate staging marker '{markerId}'.");
                    entry.Staging = (path, marker);
                }
                else
                {
                    if (entry.Final is not null)
                        throw new InvalidDataException(
                            $"Duplicate final marker '{markerId}'.");
                    entry.Final = (path, marker);
                }
            }

            var markers = new List<ReconciliationMarker>(artifacts.Count);
            foreach (var (markerId, entry) in artifacts.OrderBy(
                static pair => pair.Key, StringComparer.Ordinal))
            {
                if (entry.Final is { } final && entry.Staging is { } staging)
                {
                    if (final.Marker != staging.Marker)
                        throw new InvalidDataException(
                            $"Conflicting final/staging reconciliation marker '{markerId}'.");
                    File.Delete(staging.Path);
                    _directoryDurability.Flush(_root);
                    markers.Add(final.Marker);
                }
                else if (entry.Final is { } finalOnly)
                {
                    markers.Add(finalOnly.Marker);
                }
                else if (entry.Staging is { } stagingOnly)
                {
                    markers.Add(stagingOnly.Marker);
                }
            }
            return markers;
        }
    }

    private static ReconciliationMarker ReadMarker(
        string path,
        string expectedMarkerId)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<ReconciliationMarker>(
                File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException(
                    $"Reconciliation marker '{path}' deserialized as null.");
            if (!string.Equals(
                    marker.Id, expectedMarkerId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Reconciliation marker '{path}' id '{marker.Id}' does not match filename '{expectedMarkerId}'.");
            }
            return marker;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Corrupt reconciliation marker '{path}'.", ex);
        }
    }

    private string PathFor(string markerId)
    {
        foreach (var c in markerId)
        {
            if (!char.IsLetterOrDigit(c) && c is not '-' and not '_')
                throw new ArgumentException(
                    $"Invalid reconciliation marker id '{markerId}'.",
                    nameof(markerId));
        }
        return Path.Combine(_root, markerId + ".json");
    }

    private void CreateDirectoryPathDurably(string path)
    {
        var missing = new Stack<string>();
        var current = path;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            current = Directory.GetParent(current)?.FullName
                ?? throw new IOException(
                    $"Cannot locate existing parent for reconciliation directory '{path}'.");
        }

        while (missing.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory);
            var parent = Directory.GetParent(directory)?.FullName
                ?? throw new IOException(
                    $"Cannot fsync parent of reconciliation directory '{directory}'.");
            _directoryDurability.Flush(parent);
        }
    }

    private sealed class MarkerArtifacts
    {
        public (string Path, ReconciliationMarker Marker)? Final { get; set; }
        public (string Path, ReconciliationMarker Marker)? Staging { get; set; }
    }
}

public interface IReconciliationMarkerFileOperations
{
    void WriteAndFlush(string path, byte[] payload);
    void Move(string source, string destination);
}

internal sealed class ReconciliationMarkerFileOperations
    : IReconciliationMarkerFileOperations
{
    public static ReconciliationMarkerFileOperations Instance { get; } = new();

    public void WriteAndFlush(string path, byte[] payload)
    {
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    public void Move(string source, string destination) =>
        File.Move(source, destination, overwrite: true);
}

public interface IReconciliationDirectoryDurability
{
    void Flush(string directoryPath);
}

internal sealed class ReconciliationDirectoryDurability
    : IReconciliationDirectoryDurability
{
    public static ReconciliationDirectoryDurability Instance { get; } = new();

    public void Flush(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()
            && !OperatingSystem.IsFreeBSD())
        {
            throw new PlatformNotSupportedException(
                "Durable reconciliation markers require directory fsync support.");
        }

        var fd = Open(directoryPath, flags: 0);
        if (fd < 0)
            throw NativeIOException("open", directoryPath);
        try
        {
            if (Fsync(fd) != 0)
                throw NativeIOException("fsync", directoryPath);
        }
        finally
        {
            if (Close(fd) != 0)
                throw NativeIOException("close", directoryPath);
        }
    }

    private static IOException NativeIOException(string operation, string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException(
            $"{operation} failed for directory '{path}': " +
            new Win32Exception(error).Message);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
