namespace B3.Trading.Application;

/// <summary>
/// Optional opening cash balances applied to <see cref="CashLedger"/>
/// at process startup. Mirrors <see cref="PositionSeedOptions"/> in
/// shape and lifecycle: applied <b>after</b> snapshot/WAL recovery,
/// so a warm restart preserves real settled cash and never overwrites
/// fills with the seed.
///
/// <para>
/// Slice 1 of issue #107 — the dogfood path. Without this seed a
/// fresh end-client created via <c>POST /auth/signup</c> would have
/// zero cash; signup integration in slice 3 will pre-fund new accounts
/// from this same option.
/// </para>
/// </summary>
public sealed class CashSeedOptions
{
    public const string SectionName = "Trading:Cash";

    /// <summary>
    /// Per-(firm, end-client) opening balance. List shape (rather than nested
    /// dict) keeps env-var binding ergonomic:
    /// <c>Trading__Cash__Seeds__0__EndClientId=alice</c>.
    /// </summary>
    public List<CashSeed> Seeds { get; set; } = new();

    /// <summary>
    /// Default opening balance applied to any end-client that signs up
    /// via <c>POST /auth/signup</c> in slice 3. Currently informational
    /// for slice 1 (signup wiring lands in slice 3); kept here so the
    /// config surface is settled before consumers depend on it.
    /// </summary>
    public decimal SignupInitialBalance { get; set; }
}

public sealed class CashSeed
{
    /// <summary>
    /// Firm bucket to fund. Defaults to the legacy single-firm bucket so
    /// existing configuration remains loadable without duplicating cash into
    /// every configured firm.
    /// </summary>
    public string FirmId { get; set; } = CashLedger.DefaultFirmId;

    public string EndClientId { get; set; } = string.Empty;

    /// <summary>
    /// Opening cash. Negative values are accepted by
    /// <see cref="CashLedger"/> (the ledger doesn't enforce solvency
    /// — that's the margin provider's job in slice 2), but seeding a
    /// negative balance is almost certainly a config typo.
    /// </summary>
    public decimal InitialAvailable { get; set; }
}
