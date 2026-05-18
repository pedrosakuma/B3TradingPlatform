using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q4.1 (#301). Parallel realized-P&amp;L store keyed by
/// <c>(FirmId, EndClientId, SubAccountId, Symbol, Day)</c> for
/// sub-account segregation. Fed by <see cref="ExecutionReportProcessor"/>
/// alongside the master <see cref="PnlKeeper"/> whenever the
/// originating order carries a non-null
/// <see cref="SubAccountId"/>; the master keeper continues to
/// receive every fill (sub-account-null and sub-account-tagged
/// alike) so that endpoint reads without a filter naturally show
/// the aggregate.
///
/// <para>
/// <b>Firm namespace.</b> The same login under FIRM01 and FIRM02
/// with the same <c>SubAccountId</c> MUST NOT share realized P&amp;L
/// — sub-accounts are scoped per-firm, mirroring
/// <see cref="SubAccountsRegistry"/>.
/// </para>
/// </summary>
public sealed class SubAccountPnlKeeper
{
    private readonly ConcurrentDictionary<(string FirmId, string EndClient, string SubAccount, string Symbol, DateOnly Day), decimal>
        _realized = new();

    /// <summary>
    /// Adds <paramref name="delta"/> to the (firm, owner, subAccount,
    /// symbol, day) bucket. Idempotence is the caller's
    /// responsibility — the master <see cref="PnlKeeper"/> already
    /// dedupes on <see cref="Persistence.RealizedPnlEvent.ExecutionId"/>
    /// and gates its sub-account peer behind the same gate, so a
    /// double-apply here would require a double-apply on the master
    /// keeper too. Tracking a second seen-set would add storage for
    /// no marginal protection.
    /// </summary>
    public void Add(string firmId, string endClient, SubAccountId subAccount, string symbol, DateOnly day, decimal delta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(subAccount);
        _realized.AddOrUpdate((firmId, endClient, subAccount.Value, symbol, day),
            _ => delta,
            (_, prev) => prev + delta);
    }

    public decimal GetDayRealized(string firmId, string endClient, SubAccountId subAccount, string symbol, DateOnly day) =>
        _realized.TryGetValue((firmId, endClient, subAccount.Value, symbol, day), out var v) ? v : 0m;

    public IEnumerable<(string Symbol, decimal Realized)> ForSubAccountDay(string firmId, string endClient, SubAccountId subAccount, DateOnly day)
    {
        foreach (var kv in _realized)
            if (string.Equals(kv.Key.FirmId, firmId, StringComparison.Ordinal)
                && kv.Key.EndClient == endClient
                && kv.Key.SubAccount == subAccount.Value
                && kv.Key.Day == day)
                yield return (kv.Key.Symbol, kv.Value);
    }

    /// <summary>
    /// Lock-side snapshot capture for the two-phase pipeline.
    /// </summary>
    public Persistence.SubAccountPnlSnapshot[] Snapshot()
    {
        var pairs = _realized.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.SubAccountPnlSnapshot>();
        var arr = new Persistence.SubAccountPnlSnapshot[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
            arr[i] = new Persistence.SubAccountPnlSnapshot(
                pairs[i].Key.FirmId, pairs[i].Key.EndClient, pairs[i].Key.SubAccount,
                pairs[i].Key.Symbol, pairs[i].Key.Day, pairs[i].Value);
        return arr;
    }

    public void Restore(IEnumerable<Persistence.SubAccountPnlSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _realized.Clear();
        foreach (var s in snaps)
            _realized[(s.FirmId, s.EndClientId, s.SubAccountId, s.Symbol, s.Day)] = s.RealizedTotal;
    }
}
