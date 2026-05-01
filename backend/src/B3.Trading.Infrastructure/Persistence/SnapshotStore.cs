using System.Globalization;
using System.Text.Json;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Reads and writes <see cref="PlatformSnapshot"/> JSON files plus the
/// <c>latest.txt</c> pointer. Atomic write via temp + <see cref="File.Move(string,string,bool)"/>
/// so a crash mid-write never leaves a partial snapshot file in place.
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _root;

    public SnapshotStore(string dataDirectory, string firmId)
    {
        _root = Path.Combine(dataDirectory, firmId, "snapshots");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public void Write(PlatformSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var name = $"snap-{snapshot.Seq.ToString("D12", CultureInfo.InvariantCulture)}.json";
        var path = Path.Combine(_root, name);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
        File.Move(tmp, path, overwrite: true);
        File.WriteAllText(Path.Combine(_root, "latest.txt"),
            snapshot.Seq.ToString(CultureInfo.InvariantCulture));
    }

    public PlatformSnapshot? LoadLatest()
    {
        var pointer = Path.Combine(_root, "latest.txt");
        long? targetSeq = null;
        if (File.Exists(pointer) &&
            long.TryParse(File.ReadAllText(pointer).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            targetSeq = s;
        }

        // Pick the highest seq snapshot file regardless of latest.txt; the
        // pointer is just a hint. Scanning is cheap (snapshots roll up to
        // tens of files at most over a year).
        var files = Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "snap-*.json").ToList()
            : new List<string>();
        if (files.Count == 0) return null;

        files.Sort(StringComparer.Ordinal);
        var chosen = targetSeq is null
            ? files[^1]
            : files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith(
                  targetSeq.Value.ToString("D12", CultureInfo.InvariantCulture), StringComparison.Ordinal))
              ?? files[^1];

        try
        {
            return JsonSerializer.Deserialize<PlatformSnapshot>(File.ReadAllBytes(chosen), JsonOptions);
        }
        catch (JsonException)
        {
            // Corrupt snapshot — fall back to the previous one if any.
            files.Remove(chosen);
            if (files.Count == 0) return null;
            return JsonSerializer.Deserialize<PlatformSnapshot>(File.ReadAllBytes(files[^1]), JsonOptions);
        }
    }
}
