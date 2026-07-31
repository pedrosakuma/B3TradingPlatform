using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q4.1 (#301). Parallel position store keyed by
/// <c>(FirmId, EndClientId, SubAccountId, Symbol)</c> for sub-account
/// segregation. Fed by every fill whose originating order carries a
/// non-null <see cref="SubAccountId"/>; the existing
/// <see cref="PositionKeeper"/> continues to track the cross-account
/// aggregate so the master view is naturally available without
/// summing.
///
/// <para>
/// <b>Firm namespace.</b> The same login under FIRM01 and FIRM02 with
/// the same <c>SubAccountId</c> (e.g. <c>tradingdesk</c>) MUST NOT
/// share state — sub-accounts are scoped per-firm. The
/// <see cref="SubAccountsRegistry"/> already namespaces its rows by
/// <c>(FirmId, Id)</c>; the keepers mirror that key so multi-firm
/// hosts get clean segregation without relying on the REST validator
/// alone.
/// </para>
///
/// <para>
/// <b>Sub-account-null fills are not stored here.</b> A submission
/// without a sub-account is the legacy "master bucket" and would
/// double-count if booked into the per-sub-account map; the spec's
/// "master = aggregate of all sub-accounts + null bucket" is then
/// served by reading <see cref="PositionKeeper.ForEndClient"/>
/// (which sees every fill, including sub-account-null ones).
/// </para>
/// </summary>
public sealed class SubAccountPositionKeeper
{
    private readonly ConcurrentDictionary<(string FirmId, EndClientId Owner, string SubAccount, string Symbol), Position> _positions =
        new();

    public Position GetOrCreate(string firmId, EndClientId owner, SubAccountId subAccount, string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        return _positions.GetOrAdd((firmId, owner, subAccount.Value, symbol),
            key => new Position(key.Owner, key.Symbol));
    }

    public void ApplyFill(string firmId, EndClientId owner, SubAccountId subAccount, string symbol, OrderSide side, long quantity, decimal price)
    {
        var position = GetOrCreate(firmId, owner, subAccount, symbol);
        lock (position)
        {
            position.ApplyFill(side, quantity, price);
        }
    }

    /// <summary>
    /// Rows for one sub-account under one owner within a firm.
    /// Includes flat positions (callers are expected to filter if
    /// they care).
    /// </summary>
    public IReadOnlyCollection<Position> ForSubAccount(string firmId, EndClientId owner, SubAccountId subAccount)
    {
        var list = new List<Position>();
        foreach (var kv in _positions)
        {
            if (!string.Equals(kv.Key.FirmId, firmId, StringComparison.Ordinal)) continue;
            if (kv.Key.Owner != owner) continue;
            if (!string.Equals(kv.Key.SubAccount, subAccount.Value, StringComparison.Ordinal)) continue;
            list.Add(kv.Value);
        }
        return list;
    }

    /// <summary>
    /// Rows for every sub-account under one owner within a firm,
    /// tagged with the sub-account id. Used by <c>GET /api/positions</c>
    /// when no filter is supplied and the response wants
    /// per-sub-account breakdowns.
    /// </summary>
    public IReadOnlyList<(SubAccountId SubAccount, Position Position)> EnumerateForOwner(string firmId, EndClientId owner)
    {
        var list = new List<(SubAccountId, Position)>();
        foreach (var kv in _positions)
        {
            if (!string.Equals(kv.Key.FirmId, firmId, StringComparison.Ordinal)) continue;
            if (kv.Key.Owner != owner) continue;
            list.Add((new SubAccountId(kv.Key.SubAccount), kv.Value));
        }
        return list;
    }

    /// <summary>
    /// Lock-side capture for the snapshot pipeline. Skips flat
    /// positions, matching <see cref="PositionKeeper.RawSnapshot"/>.
    /// </summary>
    public Persistence.SubAccountPositionSnapshot[] Snapshot()
    {
        var pairs = _positions.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.SubAccountPositionSnapshot>();
        var buf = new Persistence.SubAccountPositionSnapshot[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            var p = pairs[i].Value;
            if (p.NetQuantity == 0) continue;
            buf[n++] = new Persistence.SubAccountPositionSnapshot(
                pairs[i].Key.FirmId, pairs[i].Key.Owner.Value, pairs[i].Key.SubAccount, pairs[i].Key.Symbol,
                p.NetQuantity, p.AverageEntryPrice);
        }
        if (n == buf.Length) return buf;
        var trimmed = new Persistence.SubAccountPositionSnapshot[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    public void Restore(IEnumerable<Persistence.SubAccountPositionSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _positions.Clear();
        foreach (var s in snaps)
        {
            var owner = new EndClientId(s.EndClientId);
            _positions[(s.FirmId, owner, s.SubAccountId, s.Symbol)] =
                Position.Hydrate(owner, s.Symbol, s.NetQuantity, s.AverageEntryPrice);
        }
    }

    /// <summary>
    /// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
    /// #2). Removes EVERY named-sub-account position row — across all
    /// sub-accounts and all symbols — for
    /// (<paramref name="firmId"/>, <paramref name="owner"/>). A whole-
    /// account reset changes the aggregate <see cref="PositionKeeper"/>
    /// position outright, so any named sub-account row's
    /// (NetQuantity, AverageEntryPrice) would otherwise reference a
    /// position that no longer exists post-reset — the same stale-
    /// risk-state concern that motivates
    /// <see cref="SubAccountPnlKeeper.ClearAllBucketsForAccount"/>.
    /// Sub-account-null fills are never stored here (see class-level
    /// remarks), so this has no effect on the master aggregate itself
    /// — that is reset via <see cref="PositionKeeper.SetAbsolute"/>.
    /// </summary>
    public void ClearAllForAccount(string firmId, EndClientId owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        foreach (var key in _positions.Keys)
        {
            if (string.Equals(key.FirmId, firmId, StringComparison.Ordinal) && key.Owner == owner)
                _positions.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
    /// #2). Captures every named sub-account position row currently
    /// tracked for (<paramref name="firmId"/>, <paramref name="owner"/>)
    /// so the admin reset endpoint's <c>DispatchWithPreApply</c>
    /// rollback path can restore EXACTLY the pre-reset row set if the
    /// WAL append later fails. Flat (<c>NetQuantity == 0</c>) rows are
    /// skipped — same convention as <see cref="Snapshot"/> — since a
    /// symbol the account never held for that sub-account round-trips
    /// through <see cref="RestoreForAccount"/> without needing an
    /// explicit zero entry. Paired with <see cref="RestoreForAccount"/>.
    /// </summary>
    public IReadOnlyList<SubAccountPositionEntry> SnapshotForAccount(string firmId, EndClientId owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        List<SubAccountPositionEntry>? buf = null;
        foreach (var kv in _positions)
        {
            if (!string.Equals(kv.Key.FirmId, firmId, StringComparison.Ordinal) || kv.Key.Owner != owner)
                continue;
            if (kv.Value.NetQuantity == 0) continue;
            buf ??= new List<SubAccountPositionEntry>();
            buf.Add(new SubAccountPositionEntry(
                kv.Key.SubAccount, kv.Key.Symbol, kv.Value.NetQuantity, kv.Value.AverageEntryPrice));
        }
        return (IReadOnlyList<SubAccountPositionEntry>?)buf ?? Array.Empty<SubAccountPositionEntry>();
    }

    /// <summary>
    /// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
    /// #2). Rollback companion to <see cref="SnapshotForAccount"/>:
    /// clears every named sub-account row currently tracked for the
    /// account, then reinserts exactly <paramref name="entries"/> —
    /// restoring the precise pre-reset row set after a failed WAL
    /// append.
    /// </summary>
    public void RestoreForAccount(
        string firmId, EndClientId owner, IReadOnlyList<SubAccountPositionEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentNullException.ThrowIfNull(entries);
        ClearAllForAccount(firmId, owner);
        foreach (var entry in entries)
        {
            if (entry.NetQuantity == 0) continue;
            _positions[(firmId, owner, entry.SubAccount, entry.Symbol)] =
                Position.Hydrate(owner, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
        }
    }
}

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
/// #2). One named-sub-account position row captured by
/// <see cref="SubAccountPositionKeeper.SnapshotForAccount"/> and
/// replayed by <see cref="SubAccountPositionKeeper.RestoreForAccount"/>.
/// </summary>
public sealed record SubAccountPositionEntry(string SubAccount, string Symbol, long NetQuantity, decimal AverageEntryPrice);
