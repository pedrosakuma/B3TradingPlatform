using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q4.1 (#301). In-memory registry of known sub-accounts per firm.
/// Seeded from the WAL — every <see cref="SubAccountCreatedEvent"/>
/// and <see cref="SubAccountDeactivatedEvent"/> is replayed through
/// <see cref="ApplyCreated"/> / <see cref="ApplyDeactivated"/> on
/// recovery so a snapshot+tail restart converges on the same
/// registry state. Snapshotted as a flat list via
/// <see cref="Snapshot"/> for the two-phase capture pipeline.
///
/// <para>
/// <b>Per-firm namespace.</b> Sub-accounts are scoped per-firm
/// (FIRM01:tradingdesk and FIRM02:tradingdesk are distinct). The
/// keys here are <c>(firmId, id)</c> tuples; lookups for the submit
/// pipeline always carry both. Soft-delete: deactivated entries
/// stay in the map (so historical orders still resolve) but
/// <see cref="IsActive"/> returns <c>false</c> and re-issuing a
/// <see cref="SubAccountCreatedEvent"/> for the same id revives the
/// entry (with a possibly updated display name).
/// </para>
/// </summary>
public sealed class SubAccountsRegistry
{
    private readonly ConcurrentDictionary<(string Firm, string Id), Entry> _entries =
        new(KeyEqualityComparer.Instance);

    public sealed record Entry(string FirmId, string Id, string? DisplayName, bool Active);

    /// <summary>
    /// Idempotent. Returns <c>true</c> when the call actually
    /// created (or revived) the entry; <c>false</c> when it
    /// re-activated an already-active row without changes.
    /// </summary>
    public bool ApplyCreated(string firmId, string id, string? displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var key = (firmId, id);
        var entry = new Entry(firmId, id, displayName, Active: true);
        var prev = _entries.AddOrUpdate(key, _ => entry,
            (_, _) => entry);
        return !object.ReferenceEquals(prev, entry) || prev.Active is false;
    }

    /// <summary>
    /// Soft-delete. Returns <c>true</c> when the entry was active
    /// before the call; <c>false</c> when it was already deactivated
    /// or missing entirely.
    /// </summary>
    public bool ApplyDeactivated(string firmId, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var key = (firmId, id);
        if (!_entries.TryGetValue(key, out var prev))
            return false;
        if (!prev.Active)
            return false;
        _entries[key] = prev with { Active = false };
        return true;
    }

    public bool TryGet(string firmId, string id, out Entry entry)
    {
        if (_entries.TryGetValue((firmId, id), out var found))
        {
            entry = found;
            return true;
        }
        entry = default!;
        return false;
    }

    /// <summary>True when the id is known AND not soft-deleted.</summary>
    public bool IsActive(string firmId, string id) =>
        TryGet(firmId, id, out var e) && e.Active;

    /// <summary>
    /// Lists every entry for <paramref name="firmId"/>, active or
    /// deactivated. The caller filters in the API surface as needed
    /// (default <c>GET /api/sub-accounts</c> hides deactivated rows).
    /// </summary>
    public IReadOnlyList<Entry> ListForFirm(string firmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        var list = new List<Entry>();
        foreach (var kv in _entries)
            if (string.Equals(kv.Key.Firm, firmId, StringComparison.Ordinal))
                list.Add(kv.Value);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    /// <summary>
    /// Lock-side capture for the snapshot pipeline (RFC §5.8 / P6).
    /// Caller must hold <c>EventDispatcher.WithSnapshotLock</c>;
    /// returns a deterministically-ordered array independent of
    /// concurrent <see cref="ApplyCreated"/> / <see cref="ApplyDeactivated"/>.
    /// </summary>
    public SubAccountSnapshot[] Snapshot()
    {
        var rows = _entries.ToArray();
        if (rows.Length == 0) return Array.Empty<SubAccountSnapshot>();
        Array.Sort(rows, static (a, b) =>
        {
            var cmp = string.CompareOrdinal(a.Key.Firm, b.Key.Firm);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Key.Id, b.Key.Id);
        });
        var arr = new SubAccountSnapshot[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var e = rows[i].Value;
            arr[i] = new SubAccountSnapshot(e.FirmId, e.Id, e.DisplayName, e.Active);
        }
        return arr;
    }

    /// <summary>
    /// Restores the registry from a snapshot row set. Replaces every
    /// existing entry (snapshot is authoritative — replay folds the
    /// tail back in on top via <see cref="ApplyCreated"/> /
    /// <see cref="ApplyDeactivated"/>).
    /// </summary>
    public void Restore(IEnumerable<SubAccountSnapshot> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries.Clear();
        foreach (var s in entries)
            _entries[(s.FirmId, s.Id)] = new Entry(s.FirmId, s.Id, s.DisplayName, s.Active);
    }

    private sealed class KeyEqualityComparer : IEqualityComparer<(string Firm, string Id)>
    {
        public static readonly KeyEqualityComparer Instance = new();

        public bool Equals((string Firm, string Id) x, (string Firm, string Id) y) =>
            string.Equals(x.Firm, y.Firm, StringComparison.Ordinal)
            && string.Equals(x.Id, y.Id, StringComparison.Ordinal);

        public int GetHashCode((string Firm, string Id) obj) =>
            HashCode.Combine(obj.Firm, obj.Id);
    }
}
