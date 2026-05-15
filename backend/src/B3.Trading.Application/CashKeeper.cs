using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q2.2 (#269). Per-end-client cash balance projected from the
/// <see cref="CashLedgerEvent"/> WAL stream — i.e. operator-driven
/// deposits and withdrawals only. Single-currency in v0 (BRL); the
/// ledger here intentionally does NOT fold ER fills into the same
/// projection (those continue to feed <see cref="CashLedger"/> for the
/// margin pipeline). The fill+fee composite balance is the P&amp;L
/// engine's responsibility (#271) and lands later.
///
/// <para>
/// Snapshot/restore mirrors <see cref="CashLedger"/>: zero-balance rows
/// are skipped because they re-materialise on the next event; negative
/// balances cannot occur here (the admin endpoint clamps withdrawals at
/// the available balance — see <see cref="TryWithdraw"/>).
/// </para>
/// </summary>
public sealed class CashKeeper
{
    private readonly ConcurrentDictionary<EndClientId, decimal> _balances = new();

    public decimal GetAvailable(EndClientId owner) =>
        _balances.TryGetValue(owner, out var b) ? b : 0m;

    public void ApplyDeposit(EndClientId owner, decimal amount)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "deposit amount must be > 0");
        _balances.AddOrUpdate(owner, amount, (_, current) => current + amount);
    }

    /// <summary>
    /// Atomically debits <paramref name="amount"/> iff the balance is
    /// sufficient. Returns <c>false</c> WITHOUT mutating state if the
    /// withdrawal would drive the balance negative — the caller is
    /// expected to surface a 422 to the operator. Idempotent on the
    /// failure path (no allocation, no entry creation).
    /// </summary>
    public bool TryWithdraw(EndClientId owner, decimal amount)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "withdrawal amount must be > 0");
        while (true)
        {
            if (!_balances.TryGetValue(owner, out var current))
                return false;
            if (current < amount)
                return false;
            var next = current - amount;
            if (_balances.TryUpdate(owner, next, current))
                return true;
        }
    }

    /// <summary>
    /// Replay-time apply: mirrors the live admin path exactly. Unknown
    /// <paramref name="kind"/> strings are rejected so a malformed WAL
    /// segment fails loudly instead of silently dropping the event.
    /// Withdrawals during replay are NOT validated against the running
    /// balance — by construction the live path only ever appended
    /// withdrawals that succeeded, so replay can apply them blindly. A
    /// negative balance after replay would indicate WAL corruption and
    /// is intentionally allowed to propagate (no clamp) so it surfaces
    /// in observability instead of being masked.
    /// </summary>
    public void Apply(string kind, EndClientId owner, decimal amount)
    {
        if (string.Equals(kind, "Deposit", StringComparison.Ordinal))
        {
            _balances.AddOrUpdate(owner, amount, (_, current) => current + amount);
            return;
        }
        if (string.Equals(kind, "Withdrawal", StringComparison.Ordinal))
        {
            _balances.AddOrUpdate(owner, -amount, (_, current) => current - amount);
            return;
        }
        throw new InvalidOperationException($"Unknown CashLedgerEvent kind: {kind}");
    }

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8). Caller must hold <c>EventDispatcher.WithSnapshotLock</c>.
    /// </summary>
    public CashKeeperRaw[] RawSnapshot()
    {
        var pairs = _balances.ToArray();
        if (pairs.Length == 0) return Array.Empty<CashKeeperRaw>();
        var buf = new CashKeeperRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            if (pairs[i].Value == 0m) continue;
            buf[n++] = new CashKeeperRaw(pairs[i].Key.Value, pairs[i].Value);
        }
        if (n == buf.Length) return buf;
        var trimmed = new CashKeeperRaw[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    public void Restore(IReadOnlyDictionary<string, decimal> snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        _balances.Clear();
        foreach (var kv in snap)
            _balances[new EndClientId(kv.Key)] = kv.Value;
    }
}
