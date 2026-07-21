namespace B3.Trading.Domain;

/// <summary>
/// Per-end-client cash balance, derived from the ER fill stream. T+0
/// settlement: every Buy fill debits <c>fillQty · fillPrice</c> from
/// <see cref="Available"/>; every Sell fill credits the same amount.
/// Negative balances are allowed at the domain level (the gating is the
/// margin provider's job, not this aggregate's).
///
/// <para>
/// Currency is implicit single-currency (BRL) for v1 — multi-currency
/// is explicitly out of scope (see issue #107).
/// </para>
/// </summary>
public sealed class CashBalance
{
    public CashBalance(EndClientId owner)
    {
        Owner = owner;
    }

    public EndClientId Owner { get; }

    /// <summary>
    /// Free cash that can settle new Buys. Reserve-on-submit accounting
    /// (slice 2) will overlay a "reserved" notion on top, but this
    /// aggregate stays a pure ledger of settled cash.
    /// </summary>
    public decimal Available { get; private set; }

    public void ApplyFill(OrderSide side, long quantity, decimal price)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (price < 0m)
            throw new ArgumentOutOfRangeException(nameof(price));

        var notional = price * (decimal)quantity;
        Available += side == OrderSide.Buy ? -notional : notional;
    }

    /// <summary>
    /// #387. Debit a brokerage / settlement fee from <see cref="Available"/>.
    /// Fees are always a cost — the sign is fixed (debit). Caller
    /// (CashLedger.ApplyFee, gated by FeeKeeper's seen-set) is
    /// responsible for replay idempotency.
    /// </summary>
    public void ApplyFee(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "fee must be non-negative");
        Available -= amount;
    }

    /// <summary>
    /// #679. Credit an operator- or self-service-driven cash movement
    /// (admin <c>/admin/cash</c> deposit, or sandbox self-deposit) into
    /// <see cref="Available"/> — i.e. the same spendable balance the
    /// margin provider and <c>GET /balance</c> read. Distinct from
    /// <see cref="ApplyFill"/> (fill-driven) and unconditional like
    /// <see cref="ApplyFee"/>; caller owns idempotency/audit.
    /// </summary>
    public void ApplyDeposit(decimal amount)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "deposit amount must be > 0");
        Available += amount;
    }

    /// <summary>
    /// #679. Mirror of <see cref="ApplyDeposit"/> for operator-driven
    /// withdrawals. Unconditional — the insufficient-funds gate lives
    /// upstream (<c>CashKeeper.TryWithdraw</c> is the authoritative
    /// check for <c>/admin/cash</c>); this just keeps the spendable
    /// balance consistent with that decision.
    /// </summary>
    public void ApplyWithdrawal(decimal amount)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "withdrawal amount must be > 0");
        Available -= amount;
    }

    /// <summary>
    /// Recovery / seed-only constructor used by snapshot replay and
    /// startup seeding. Skips the ApplyFill arithmetic so a snapshot
    /// loaded with a negative or zero balance round-trips exactly.
    /// </summary>
    internal static CashBalance Hydrate(EndClientId owner, decimal available)
    {
        return new CashBalance(owner) { Available = available };
    }
}
