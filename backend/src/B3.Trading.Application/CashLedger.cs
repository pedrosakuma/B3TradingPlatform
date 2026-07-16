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
    /// #386. Fired AFTER every mutation that changes
    /// <see cref="CashBalance.Available"/> for an owner — fills, fees,
    /// and the opening-balance seed. Invoked under the per-balance
    /// lock so subscribers observe a consistent (owner, newAvailable)
    /// pair, in mutation order. Listeners must NOT block (the lock is
    /// held); the WS fan-out enqueues onto a channel and returns.
    /// </summary>
    public event Action<EndClientId, decimal>? BalanceChanged;

    private void RaiseBalanceChanged(EndClientId owner, decimal newAvailable)
    {
        var handler = BalanceChanged;
        if (handler is null) return;
        try { handler(owner, newAvailable); }
        catch { /* one bad subscriber must not poison the keeper */ }
    }

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
        if (_balances.TryAdd(owner, seeded))
        {
            RaiseBalanceChanged(owner, initialAvailable);
            return true;
        }
        return false;
    }

    public void ApplyFill(EndClientId owner, OrderSide side, long quantity, decimal price)
    {
        var balance = GetOrCreate(owner);
        decimal newAvailable;
        lock (balance)
        {
            balance.ApplyFill(side, quantity, price);
            newAvailable = balance.Available;
            RaiseBalanceChanged(owner, newAvailable);
        }
    }

    /// <summary>
    /// #387. Debit a brokerage / settlement fee from <see cref="CashBalance.Available"/>.
    /// Called by <see cref="FeeKeeper.Apply"/> after the seen-set
    /// guard succeeds, so this method itself is NOT idempotent — it
    /// always debits the supplied <paramref name="amount"/>. Replay
    /// idempotency lives in the keeper (FeeAccruedEvent.ExecutionId
    /// dedup); from there it's the same byte-identical recovery
    /// contract as <see cref="ApplyFill"/>.
    /// <para>
    /// <paramref name="amount"/> is a positive fee total (the
    /// <see cref="Persistence.FeeAccruedEvent.Total"/> field is always
    /// non-negative). Zero amounts are a no-op so a fee-free symbol
    /// does not materialise a balance row.
    /// </para>
    /// </summary>
    public void ApplyFee(EndClientId owner, decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "fee must be non-negative");
        if (amount == 0m) return;
        var balance = GetOrCreate(owner);
        lock (balance)
        {
            balance.ApplyFee(amount);
            RaiseBalanceChanged(owner, balance.Available);
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
            yield return new Persistence.CashBalanceSnapshot(kv.Key.Value, kv.Value.Available);
        }
    }

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8 / P6). Caller must hold
    /// <c>EventDispatcher.WithSnapshotLock</c> so the <c>Available</c>
    /// reads reflect the snapshot's <c>seq</c> (RFC §4.3). Materialised
    /// zero balances are preserved because they prevent configured opening
    /// seeds from being reapplied on restart.
    /// </summary>
    public Persistence.CashRaw[] RawSnapshot()
    {
        var pairs = _balances.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.CashRaw>();
        var buf = new Persistence.CashRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            var bal = pairs[i].Value;
            buf[n++] = new Persistence.CashRaw(pairs[i].Key.Value, bal.Available);
        }
        return buf;
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
