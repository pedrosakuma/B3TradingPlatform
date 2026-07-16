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

    public FileReconciliationMarkerStore(IOptions<PersistenceOptions> options)
        : this(options.Value)
    {
    }

    public FileReconciliationMarkerStore(PersistenceOptions options)
        : this(options, ReconciliationDirectoryDurability.Instance)
    {
    }

    public FileReconciliationMarkerStore(
        PersistenceOptions options,
        IReconciliationDirectoryDurability directoryDurability)
    {
        _directoryDurability = directoryDurability
            ?? throw new ArgumentNullException(nameof(directoryDurability));
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
            var payload = JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions);
            using (var stream = new FileStream(
                staging, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
            _directoryDurability.Flush(_root);
        }
    }

    public void Remove(string markerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerId);
        lock (_lock)
        {
            var path = PathFor(markerId);
            if (File.Exists(path))
            {
                File.Delete(path);
                _directoryDurability.Flush(_root);
            }
        }
    }

    public IReadOnlyList<ReconciliationMarker> Load()
    {
        lock (_lock)
        {
            var markers = new List<ReconciliationMarker>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                var marker = JsonSerializer.Deserialize<ReconciliationMarker>(
                    File.ReadAllBytes(path), JsonOptions)
                    ?? throw new InvalidDataException(
                        $"Reconciliation marker '{path}' deserialized as null.");
                markers.Add(marker);
            }
            return markers;
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
