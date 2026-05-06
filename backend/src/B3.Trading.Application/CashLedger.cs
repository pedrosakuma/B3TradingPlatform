using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Cumulative cash ledger, derived from <c>ExecutionReport</c> fills via
/// <see cref="ExecutionReportProcessor"/>. Per-end-client; ephemeral in
/// v1 but reconstructed from snapshot + ER replay on cold start, so the
/// final number after recovery is byte-identical to the live state.
///
/// <para>
/// Slice 1 of issue #107 — exposes the balance via a read-only API and a
/// startup seed. Margin integration (slice 2) plugs into
/// <see cref="GetAvailable(EndClientId)"/> through a separate provider.
/// </para>
/// </summary>
public sealed class CashLedger
{
    private readonly ConcurrentDictionary<EndClientId, CashBalance> _balances = new();

    /// <summary>
    /// Returns the existing balance or creates a fresh one at zero. Used
    /// by ER processor to fold fills lazily — accounts that never fill
    /// stay out of memory.
    /// </summary>
    public CashBalance GetOrCreate(EndClientId owner) =>
        _balances.GetOrAdd(owner, key => new CashBalance(key));

    /// <summary>
    /// Insert an opening balance iff one is not already tracked. Returns
    /// <c>true</c> when the seed was applied; <c>false</c> when an
    /// existing balance (from snapshot/WAL replay or a prior fill)
    /// already occupies the slot. Idempotent and thread-safe.
    /// </summary>
    public bool SeedIfAbsent(EndClientId owner, decimal initialAvailable)
    {
        var seeded = CashBalance.Hydrate(owner, initialAvailable);
        return _balances.TryAdd(owner, seeded);
    }

    public void ApplyFill(EndClientId owner, OrderSide side, long quantity, decimal price)
    {
        var balance = GetOrCreate(owner);
        lock (balance)
        {
            balance.ApplyFill(side, quantity, price);
        }
    }

    /// <summary>
    /// Read-only convenience for risk / API callers. Returns <c>0</c> for
    /// an unknown owner without materialising an entry, so probing the
    /// balance can't pollute the dictionary.
    /// </summary>
    public decimal GetAvailable(EndClientId owner) =>
        _balances.TryGetValue(owner, out var b) ? b.Available : 0m;

    public bool TryGet(EndClientId owner, out CashBalance? balance)
    {
        if (_balances.TryGetValue(owner, out var b))
        {
            balance = b;
            return true;
        }
        balance = null;
        return false;
    }

    public IEnumerable<Persistence.CashBalanceSnapshot> Snapshot()
    {
        foreach (var kv in _balances)
        {
            // Skip exact-zero rows — they re-materialise the moment a
            // fill arrives, and persisting them would bloat the snapshot
            // for no behavioural difference. Negative balances ARE
            // captured (debt is real state).
            if (kv.Value.Available == 0m) continue;
            yield return new Persistence.CashBalanceSnapshot(kv.Key.Value, kv.Value.Available);
        }
    }

    public void Restore(IEnumerable<Persistence.CashBalanceSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _balances.Clear();
        foreach (var s in snaps)
        {
            var owner = new EndClientId(s.EndClientId);
            _balances[owner] = CashBalance.Hydrate(owner, s.Available);
        }
    }
}
