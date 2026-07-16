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

    public FileReconciliationMarkerStore(IOptions<PersistenceOptions> options)
        : this(options.Value)
    {
    }

    public FileReconciliationMarkerStore(PersistenceOptions options)
    {
        _root = Path.Combine(
            options.DataDirectory, options.FirmId, "reconciliation");
        Directory.CreateDirectory(_root);
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
        }
    }

    public void Remove(string markerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerId);
        lock (_lock)
        {
            var path = PathFor(markerId);
            if (File.Exists(path))
                File.Delete(path);
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
}
