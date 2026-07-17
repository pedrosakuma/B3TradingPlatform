using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q2.2 (#269). Per-(firm, end-client) cash balance projected from the
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
    private readonly ConcurrentDictionary<AccountKey, decimal> _balances =
        new(AccountKeyComparer.Instance);

    public decimal GetAvailable(string firmId, EndClientId owner) =>
        _balances.TryGetValue(AccountKey.Create(firmId, owner), out var b) ? b : 0m;

    public decimal GetAvailable(EndClientId owner) =>
        GetAvailable(CashLedger.DefaultFirmId, owner);

    public void ApplyDeposit(string firmId, EndClientId owner, decimal amount)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "deposit amount must be > 0");
        _balances.AddOrUpdate(
            AccountKey.Create(firmId, owner), amount, (_, current) => current + amount);
    }

    public void ApplyDeposit(EndClientId owner, decimal amount) =>
        ApplyDeposit(CashLedger.DefaultFirmId, owner, amount);

    /// <summary>
    /// Atomically debits <paramref name="amount"/> iff the balance is
    /// sufficient. Returns <c>false</c> WITHOUT mutating state if the
    /// withdrawal would drive the balance negative — the caller is
    /// expected to surface a 422 to the operator. Idempotent on the
    /// failure path (no allocation, no entry creation).
    /// </summary>
    public bool TryWithdraw(string firmId, EndClientId owner, decimal amount)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "withdrawal amount must be > 0");
        var key = AccountKey.Create(firmId, owner);
        while (true)
        {
            if (!_balances.TryGetValue(key, out var current))
                return false;
            if (current < amount)
                return false;
            var next = current - amount;
            if (_balances.TryUpdate(key, next, current))
                return true;
        }
    }

    public bool TryWithdraw(EndClientId owner, decimal amount) =>
        TryWithdraw(CashLedger.DefaultFirmId, owner, amount);

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
    public void Apply(string firmId, string kind, EndClientId owner, decimal amount)
    {
        var key = AccountKey.Create(firmId, owner);
        if (string.Equals(kind, "Deposit", StringComparison.Ordinal))
        {
            _balances.AddOrUpdate(key, amount, (_, current) => current + amount);
            return;
        }
        if (string.Equals(kind, "Withdrawal", StringComparison.Ordinal))
        {
            _balances.AddOrUpdate(key, -amount, (_, current) => current - amount);
            return;
        }
        throw new InvalidOperationException($"Unknown CashLedgerEvent kind: {kind}");
    }

    public void Apply(string kind, EndClientId owner, decimal amount) =>
        Apply(CashLedger.DefaultFirmId, kind, owner, amount);

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
            buf[n++] = new CashKeeperRaw(
                pairs[i].Key.Owner.Value, pairs[i].Value, pairs[i].Key.FirmId);
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
        {
            var (firmId, endClientId) = ParseSnapshotKey(kv.Key);
            var owner = new EndClientId(endClientId);
            _balances[AccountKey.Create(firmId, owner)] = kv.Value;
        }
    }

    public static string FormatSnapshotKey(string firmId, string endClientId) =>
        $"{firmId}|{endClientId}";

    private static (string FirmId, string EndClientId) ParseSnapshotKey(string key)
    {
        var separator = key.IndexOf('|');
        return separator > 0 && separator < key.Length - 1
            ? (key[..separator], key[(separator + 1)..])
            : (CashLedger.DefaultFirmId, key);
    }

    private readonly record struct AccountKey(string FirmId, EndClientId Owner)
    {
        public static AccountKey Create(string firmId, EndClientId owner)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
            return new AccountKey(firmId, owner);
        }
    }

    private sealed class AccountKeyComparer : IEqualityComparer<AccountKey>
    {
        public static readonly AccountKeyComparer Instance = new();

        public bool Equals(AccountKey x, AccountKey y) =>
            string.Equals(x.FirmId, y.FirmId, StringComparison.OrdinalIgnoreCase)
            && x.Owner == y.Owner;

        public int GetHashCode(AccountKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FirmId),
                obj.Owner);
    }
}
