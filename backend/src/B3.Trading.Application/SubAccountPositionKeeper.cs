using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q4.1 (#301). Parallel position store keyed by
/// <c>(EndClientId, SubAccountId, Symbol)</c> for sub-account
/// segregation. Fed by every fill whose originating order carries a
/// non-null <see cref="SubAccountId"/>; the existing
/// <see cref="PositionKeeper"/> continues to track the cross-account
/// aggregate so the master view is naturally available without
/// summing.
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
    private readonly ConcurrentDictionary<(EndClientId Owner, string SubAccount, string Symbol), Position> _positions =
        new();

    public Position GetOrCreate(EndClientId owner, SubAccountId subAccount, string symbol) =>
        _positions.GetOrAdd((owner, subAccount.Value, symbol),
            key => new Position(key.Owner, key.Symbol));

    public void ApplyFill(EndClientId owner, SubAccountId subAccount, string symbol, OrderSide side, long quantity, decimal price)
    {
        var position = GetOrCreate(owner, subAccount, symbol);
        lock (position)
        {
            position.ApplyFill(side, quantity, price);
        }
    }

    /// <summary>
    /// Rows for one sub-account under one owner. Includes flat
    /// positions (callers are expected to filter if they care).
    /// </summary>
    public IReadOnlyCollection<Position> ForSubAccount(EndClientId owner, SubAccountId subAccount)
    {
        var list = new List<Position>();
        foreach (var kv in _positions)
        {
            if (kv.Key.Owner != owner) continue;
            if (!string.Equals(kv.Key.SubAccount, subAccount.Value, StringComparison.Ordinal)) continue;
            list.Add(kv.Value);
        }
        return list;
    }

    /// <summary>
    /// Rows for every sub-account under one owner, tagged with the
    /// sub-account id. Used by <c>GET /positions</c> when no filter
    /// is supplied and the response wants per-sub-account breakdowns.
    /// </summary>
    public IReadOnlyList<(SubAccountId SubAccount, Position Position)> EnumerateForOwner(EndClientId owner)
    {
        var list = new List<(SubAccountId, Position)>();
        foreach (var kv in _positions)
        {
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
                pairs[i].Key.Owner.Value, pairs[i].Key.SubAccount, pairs[i].Key.Symbol,
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
            _positions[(owner, s.SubAccountId, s.Symbol)] =
                Position.Hydrate(owner, s.Symbol, s.NetQuantity, s.AverageEntryPrice);
        }
    }
}
