using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application;

/// <summary>
/// Q2.3 (#270). Per-end-client / per-day total fees projected from the
/// <see cref="FeeAccruedEvent"/> WAL stream. Drives the daily statement
/// (#272) and feeds the P&amp;L pipeline (#271).
///
/// <para>
/// <b>Idempotence.</b> Each <see cref="FeeAccruedEvent.ExecutionId"/>
/// is the deterministic combination of <c>ClOrdId + cumulative quantity
/// after the fill</c> (see <see cref="FeeAccruedEvent.ExecutionId"/>);
/// the keeper guards <see cref="Apply"/> with a seen-set so re-applying
/// the same event (FIXP retransmit, WAL replay) cannot double-charge
/// the running totals. The seen-set is captured into the snapshot too —
/// that way a snapshot+tail recovery ends in the same state as a
/// WAL-only replay.
/// </para>
///
/// <para>
/// <b>Day boundary.</b> The day key is derived from
/// <c>FeeAccruedEvent.TimestampUtc</c> as <c>DateOnly.FromDateTime(ts.UtcDateTime)</c> —
/// UTC by construction (matches every other audit timestamp in the
/// platform; the BR session boundary handling lives in the statement
/// projection, not in the keeper).
/// </para>
/// </summary>
public sealed class FeeKeeper
{
    private readonly ConcurrentDictionary<(string EndClient, DateOnly Day), decimal> _totals = new();
    private readonly ConcurrentDictionary<string, byte> _seenExecutionIds = new();

    public decimal GetDayTotal(string endClient, DateOnly day) =>
        _totals.TryGetValue((endClient, day), out var t) ? t : 0m;

    /// <summary>
    /// Folds <paramref name="evt"/> into the running totals. Idempotent
    /// on <see cref="FeeAccruedEvent.ExecutionId"/>: a re-applied event
    /// with the same id is a no-op. Returns <c>true</c> when the event
    /// advanced the totals; <c>false</c> on a duplicate.
    /// </summary>
    public bool Apply(FeeAccruedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!_seenExecutionIds.TryAdd(evt.ExecutionId, 0)) return false;
        var day = DateOnly.FromDateTime(evt.TimestampUtc.UtcDateTime);
        var key = (evt.EndClientId, day);
        _totals.AddOrUpdate(key, evt.Total, (_, current) => current + evt.Total);
        return true;
    }

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8). Caller must hold <c>EventDispatcher.WithSnapshotLock</c>.
    /// Skips zero rows because they re-materialise on the next event
    /// (same convention as <see cref="CashKeeper.RawSnapshot"/>).
    /// </summary>
    public FeeKeeperRaw[] RawSnapshot()
    {
        var pairs = _totals.ToArray();
        if (pairs.Length == 0) return Array.Empty<FeeKeeperRaw>();
        var buf = new FeeKeeperRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            if (pairs[i].Value == 0m) continue;
            buf[n++] = new FeeKeeperRaw(pairs[i].Key.EndClient, pairs[i].Key.Day, pairs[i].Value);
        }
        if (n == buf.Length) return buf;
        var trimmed = new FeeKeeperRaw[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    /// <summary>
    /// Phase-1 (lock-side) capture of the seen-set for idempotence.
    /// Persisted alongside the totals so a snapshot+tail recovery ends
    /// in the same state as a WAL-only replay (the tail's
    /// <see cref="FeeAccruedEvent"/> rows are filtered through the same
    /// guard, otherwise a snapshot taken after a fill plus a tail
    /// containing that fill's event would double-count).
    /// </summary>
    public string[] RawSnapshotSeenIds()
    {
        var ids = new string[_seenExecutionIds.Count];
        var n = 0;
        foreach (var kv in _seenExecutionIds)
        {
            if (n >= ids.Length) break;
            ids[n++] = kv.Key;
        }
        if (n == ids.Length) return ids;
        var trimmed = new string[n];
        Array.Copy(ids, trimmed, n);
        return trimmed;
    }

    public void Restore(IReadOnlyDictionary<string, decimal> totalsByKey, IEnumerable<string>? seenExecutionIds = null)
    {
        ArgumentNullException.ThrowIfNull(totalsByKey);
        _totals.Clear();
        _seenExecutionIds.Clear();
        foreach (var kv in totalsByKey)
        {
            if (!TryParseKey(kv.Key, out var endClient, out var day)) continue;
            _totals[(endClient, day)] = kv.Value;
        }
        if (seenExecutionIds is not null)
        {
            foreach (var id in seenExecutionIds)
                _seenExecutionIds.TryAdd(id, 0);
        }
    }

    /// <summary>
    /// Composite key serialisation for the snapshot's
    /// <c>Dictionary&lt;string, decimal&gt;</c> shape. Format is
    /// <c>{endClient}|{yyyy-MM-dd}</c>; the pipe is illegal in
    /// end-client ids (the API validator rejects it) so the split is
    /// unambiguous.
    /// </summary>
    public static string FormatKey(string endClient, DateOnly day) =>
        endClient + "|" + day.ToString("yyyy-MM-dd");

    public static bool TryParseKey(string key, out string endClient, out DateOnly day)
    {
        endClient = string.Empty;
        day = default;
        if (string.IsNullOrEmpty(key)) return false;
        var pipe = key.LastIndexOf('|');
        if (pipe <= 0 || pipe == key.Length - 1) return false;
        if (!DateOnly.TryParseExact(key.AsSpan(pipe + 1), "yyyy-MM-dd", out day)) return false;
        endClient = key.Substring(0, pipe);
        return true;
    }
}
