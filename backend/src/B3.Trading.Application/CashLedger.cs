using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Cumulative cash ledger, derived from <c>ExecutionReport</c> fills via
/// <see cref="ExecutionReportProcessor"/>. Per-(firm, end-client); ephemeral in
/// v1 but reconstructed from snapshot + ER replay on cold start, so the
/// final number after recovery is byte-identical to the live state. Balances
/// are scoped by (firm, end-client); the same end-client identity in two firms
/// never shares settled cash.
///
/// <para>
/// Slice 1 of issue #107 — exposes the balance via a read-only API and a
/// startup seed. Margin integration (slice 2) plugs into
/// <see cref="GetAvailable(string, EndClientId)"/> through a separate provider.
/// </para>
/// </summary>
public sealed class CashLedger
{
    public const string DefaultFirmId = "DEFAULT";

    private readonly ConcurrentDictionary<AccountKey, CashBalance> _balances =
        new(AccountKeyComparer.Instance);
    private readonly Dictionary<EndClientId, CashBalance> _unmappedLegacyBalances = new();
    private readonly Dictionary<EndClientId, string> _legacyMigrationFirms = new();
    private readonly object _legacyGate = new();

    /// <summary>
    /// #386. Fired AFTER every mutation that changes
    /// <see cref="CashBalance.Available"/> for an owner — fills, fees,
    /// and the opening-balance seed. Invoked under the per-balance
    /// lock so subscribers observe a consistent (owner, newAvailable)
    /// pair, in mutation order. Listeners must NOT block (the lock is
    /// held); the WS fan-out enqueues onto a channel and returns.
    /// </summary>
    public event Action<string, EndClientId, decimal>? BalanceChanged;

    private void RaiseBalanceChanged(string firmId, EndClientId owner, decimal newAvailable)
    {
        var handler = BalanceChanged;
        if (handler is null) return;
        try { handler(firmId, owner, newAvailable); }
        catch { /* one bad subscriber must not poison the keeper */ }
    }

    /// <summary>
    /// Returns the existing balance or creates a fresh one at zero. Used
    /// by ER processor to fold fills lazily — accounts that never fill
    /// stay out of memory.
    /// </summary>
    public CashBalance GetOrCreate(string firmId, EndClientId owner) =>
        _balances.GetOrAdd(AccountKey.Create(firmId, owner), _ => new CashBalance(owner));

    public CashBalance GetOrCreate(EndClientId owner) =>
        GetOrCreate(DefaultFirmId, owner);

    /// <summary>
    /// Insert an opening balance iff one is not already tracked. Returns
    /// <c>true</c> when the seed was applied; <c>false</c> when an
    /// existing balance (from snapshot/WAL replay or a prior fill)
    /// already occupies the slot. Idempotent and thread-safe.
    /// </summary>
    public bool SeedIfAbsent(string firmId, EndClientId owner, decimal initialAvailable)
    {
        var key = AccountKey.Create(firmId, owner);
        lock (_legacyGate)
        {
            if (_legacyMigrationFirms.TryGetValue(owner, out var migratedFirm))
            {
                if (!string.Equals(migratedFirm, firmId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Legacy cash for end-client '{owner.Value}' was migrated to firm " +
                        $"'{migratedFirm}'; refusing seed for conflicting firm '{firmId}'.");
                }
                return false;
            }
            if (_unmappedLegacyBalances.Remove(owner, out var legacy))
            {
                if (!_balances.TryAdd(key, legacy))
                {
                    throw new InvalidOperationException(
                        $"Cannot migrate legacy cash for '{owner.Value}' to '{firmId}': " +
                        "a firm-scoped balance already exists.");
                }
                _legacyMigrationFirms[owner] = firmId;
                return false;
            }
        }
        var seeded = CashBalance.Hydrate(owner, initialAvailable);
        if (_balances.TryAdd(key, seeded))
        {
            RaiseBalanceChanged(firmId, owner, initialAvailable);
            return true;
        }
        return false;
    }

    public bool SeedIfAbsent(EndClientId owner, decimal initialAvailable) =>
        SeedIfAbsent(DefaultFirmId, owner, initialAvailable);

    public void ApplyFill(
        string firmId,
        EndClientId owner,
        OrderSide side,
        long quantity,
        decimal price)
    {
        var balance = GetOrCreate(firmId, owner);
        decimal newAvailable;
        lock (balance)
        {
            balance.ApplyFill(side, quantity, price);
            newAvailable = balance.Available;
            RaiseBalanceChanged(firmId, owner, newAvailable);
        }
    }

    public void ApplyFill(EndClientId owner, OrderSide side, long quantity, decimal price) =>
        ApplyFill(DefaultFirmId, owner, side, quantity, price);

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
    public void ApplyFee(string firmId, EndClientId owner, decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "fee must be non-negative");
        if (amount == 0m) return;
        var balance = GetOrCreate(firmId, owner);
        lock (balance)
        {
            balance.ApplyFee(amount);
            RaiseBalanceChanged(firmId, owner, balance.Available);
        }
    }

    public void ApplyFee(EndClientId owner, decimal amount) =>
        ApplyFee(DefaultFirmId, owner, amount);

    /// <summary>
    /// #679. Folds an operator-driven (<c>/admin/cash</c>) or
    /// self-service sandbox cash deposit into the spendable balance —
    /// closing the gap where <c>CashKeeper</c> tracked operator cash
    /// movements in complete isolation from the balance the margin
    /// provider and <c>GET /balance</c> actually read. Unconditional
    /// (mirrors <see cref="ApplyFee"/>); callers own idempotency via the
    /// WAL event fold and any insufficient-funds gate.
    /// </summary>
    public void ApplyDeposit(string firmId, EndClientId owner, decimal amount)
    {
        var balance = GetOrCreate(firmId, owner);
        lock (balance)
        {
            balance.ApplyDeposit(amount);
            RaiseBalanceChanged(firmId, owner, balance.Available);
        }
    }

    public void ApplyDeposit(EndClientId owner, decimal amount) =>
        ApplyDeposit(DefaultFirmId, owner, amount);

    /// <summary>
    /// #679. Mirror of <see cref="ApplyDeposit(string, EndClientId, decimal)"/>
    /// for operator-driven withdrawals. The insufficient-funds check
    /// stays on <c>CashKeeper.TryWithdraw</c> (unchanged, authoritative
    /// for <c>/admin/cash</c>) — this call only keeps the spendable
    /// balance consistent with an already-approved debit.
    /// </summary>
    public void ApplyWithdrawal(string firmId, EndClientId owner, decimal amount)
    {
        var balance = GetOrCreate(firmId, owner);
        lock (balance)
        {
            balance.ApplyWithdrawal(amount);
            RaiseBalanceChanged(firmId, owner, balance.Available);
        }
    }

    public void ApplyWithdrawal(EndClientId owner, decimal amount) =>
        ApplyWithdrawal(DefaultFirmId, owner, amount);

    /// <summary>
    /// Read-only convenience for risk / API callers. Returns <c>0</c> for
    /// an unknown owner without materialising an entry, so probing the
    /// balance can't pollute the dictionary.
    /// </summary>
    public decimal GetAvailable(string firmId, EndClientId owner) =>
        _balances.TryGetValue(AccountKey.Create(firmId, owner), out var b) ? b.Available : 0m;

    public decimal GetAvailable(EndClientId owner) =>
        GetAvailable(DefaultFirmId, owner);

    public bool TryGet(string firmId, EndClientId owner, out CashBalance? balance)
    {
        if (_balances.TryGetValue(AccountKey.Create(firmId, owner), out var b))
        {
            balance = b;
            return true;
        }
        balance = null;
        return false;
    }

    public bool TryGet(EndClientId owner, out CashBalance? balance) =>
        TryGet(DefaultFirmId, owner, out balance);

    public IEnumerable<Persistence.CashBalanceSnapshot> Snapshot()
    {
        EnsureNoUnmappedLegacyBalances();
        foreach (var kv in _balances)
        {
            yield return new Persistence.CashBalanceSnapshot(
                kv.Key.Owner.Value, kv.Value.Available, kv.Key.FirmId);
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
        EnsureNoUnmappedLegacyBalances();
        var pairs = _balances.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.CashRaw>();
        var buf = new Persistence.CashRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            var bal = pairs[i].Value;
            buf[n++] = new Persistence.CashRaw(
                pairs[i].Key.Owner.Value, bal.Available, pairs[i].Key.FirmId);
        }
        return buf;
    }

    public void Restore(
        IEnumerable<Persistence.CashBalanceSnapshot> snaps,
        bool firmScoped = true,
        IReadOnlyDictionary<string, string>? legacyFirmHints = null)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _balances.Clear();
        lock (_legacyGate)
        {
            _unmappedLegacyBalances.Clear();
            _legacyMigrationFirms.Clear();
            foreach (var s in snaps)
            {
                var owner = new EndClientId(s.EndClientId);
                var balance = CashBalance.Hydrate(owner, s.Available);
                if (firmScoped
                    || !string.Equals(
                        s.FirmId,
                        DefaultFirmId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _balances[AccountKey.Create(s.FirmId, owner)] = balance;
                    continue;
                }

                if (legacyFirmHints is not null
                    && legacyFirmHints.TryGetValue(s.EndClientId, out var hintedFirm)
                    && !string.IsNullOrWhiteSpace(hintedFirm))
                {
                    _balances[AccountKey.Create(hintedFirm, owner)] = balance;
                    _legacyMigrationFirms[owner] = hintedFirm;
                    continue;
                }

                if (!_unmappedLegacyBalances.TryAdd(owner, balance))
                {
                    throw new InvalidOperationException(
                        $"Legacy cash snapshot contains duplicate balance rows for " +
                        $"end-client '{owner.Value}'.");
                }
            }
        }
    }

    public void ResolveLegacyBalances(IReadOnlyDictionary<string, string> firmByEndClient)
    {
        ArgumentNullException.ThrowIfNull(firmByEndClient);
        lock (_legacyGate)
        {
            foreach (var (endClientId, firmId) in firmByEndClient)
            {
                if (string.IsNullOrWhiteSpace(endClientId)
                    || string.IsNullOrWhiteSpace(firmId))
                {
                    continue;
                }
                var owner = new EndClientId(endClientId);
                if (_legacyMigrationFirms.TryGetValue(owner, out var migratedFirm)
                    && !string.Equals(
                        migratedFirm,
                        firmId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Conflicting firm hints for legacy cash owner '{endClientId}': " +
                        $"'{migratedFirm}' and '{firmId}'.");
                }
                if (!_unmappedLegacyBalances.Remove(owner, out var legacy))
                    continue;
                if (!_balances.TryAdd(AccountKey.Create(firmId, owner), legacy))
                {
                    throw new InvalidOperationException(
                        $"Cannot migrate legacy cash for '{endClientId}' to '{firmId}': " +
                        "a firm-scoped balance already exists.");
                }
                _legacyMigrationFirms[owner] = firmId;
            }
        }
    }

    public void EnsureNoUnmappedLegacyBalances()
    {
        lock (_legacyGate)
        {
            if (_unmappedLegacyBalances.Count == 0) return;
            var owners = string.Join(
                ", ",
                _unmappedLegacyBalances.Keys
                    .Select(static owner => owner.Value)
                    .OrderBy(static owner => owner, StringComparer.Ordinal));
            throw new InvalidOperationException(
                "Legacy owner-only cash snapshot cannot be mapped unambiguously for " +
                $"end-client(s): {owners}. Configure exactly one firm through " +
                "Trading:Auth:Users or Trading:Cash:Seeds before restarting.");
        }
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
