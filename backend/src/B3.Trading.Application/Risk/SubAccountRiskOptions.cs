namespace B3.Trading.Application.Risk;

/// <summary>
/// Q4.1 (#301). Per-(firm, subAccountId) risk caps applied IN
/// ADDITION TO the existing master caps in <see cref="RiskOptions"/>.
/// The pipeline rejects the submit if EITHER bucket fails — master
/// limits still always apply globally; sub-account limits are a
/// per-segregated-bucket extra gate.
///
/// <para>
/// <b>Config shape.</b> Bound from <c>Trading:Risk:SubAccount</c>:
/// <code>
/// {
///   "PerFirm": {
///     "FIRM01": {
///       "PerSubAccount": {
///         "tradingdesk": { "MaxOpenOrders": 50, "PositionLimit": 100000, "MaxNotional": 5000000 }
///       },
///       "Default": { "MaxOpenOrders": 25 }
///     }
///   }
/// }
/// </code>
/// Resolution: per-(firm, sub-account) → per-firm default → null
/// (no sub-account cap; only master gates apply). A missing key at
/// every level collapses to "no cap" — the check is a no-op for
/// firms / sub-accounts that have not been configured.
/// </para>
///
/// <para>
/// <b>Reason taxonomy.</b> Rejections from
/// <see cref="Checks.SubAccountLimitsCheck"/> use the
/// <c>sub_account_limit_exceeded</c> reason prefix so a metric / log
/// scan can distinguish them from the master-side rejections.
/// </para>
/// </summary>
public sealed class SubAccountRiskOptions
{
    public const string SectionName = "Trading:Risk:SubAccount";

    public Dictionary<string, FirmSubAccountRiskOptions> PerFirm { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective <see cref="SubAccountRiskLimits"/> for
    /// <paramref name="firmId"/> / <paramref name="subAccountId"/>.
    /// Returns <c>null</c> when no per-firm configuration exists for
    /// the firm at all — the check is then a complete no-op (master
    /// limits still apply via the existing pipeline).
    /// </summary>
    public SubAccountRiskLimits? Resolve(string firmId, string subAccountId)
    {
        if (string.IsNullOrWhiteSpace(firmId) || string.IsNullOrWhiteSpace(subAccountId))
            return null;
        if (!PerFirm.TryGetValue(firmId, out var firm)) return null;
        if (firm.PerSubAccount.TryGetValue(subAccountId, out var explicitCap))
            return Merge(explicitCap, firm.Default);
        return firm.Default;
    }

    private static SubAccountRiskLimits Merge(SubAccountRiskLimits primary, SubAccountRiskLimits? fallback) =>
        fallback is null
            ? primary
            : new SubAccountRiskLimits
            {
                MaxOpenOrders = primary.MaxOpenOrders ?? fallback.MaxOpenOrders,
                PositionLimit = primary.PositionLimit ?? fallback.PositionLimit,
                MaxNotional = primary.MaxNotional ?? fallback.MaxNotional,
            };
}

public sealed class FirmSubAccountRiskOptions
{
    public Dictionary<string, SubAccountRiskLimits> PerSubAccount { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public SubAccountRiskLimits? Default { get; set; }
}

public sealed class SubAccountRiskLimits
{
    /// <summary>
    /// Cap on simultaneous non-terminal orders for the sub-account
    /// (PendingNew / Working / PartiallyFilled). Mirrors the master
    /// <see cref="RiskLimits.MaxOpenOrders"/> but scoped to the
    /// per-sub-account secondary index on
    /// <see cref="WorkingOrderBook"/>.
    /// </summary>
    public int? MaxOpenOrders { get; set; }

    /// <summary>
    /// Cap on the absolute net position within the sub-account on
    /// the order's symbol. Computed exactly like the master
    /// <see cref="RiskLimits.PositionLimit"/>, but against
    /// <see cref="SubAccountPositionKeeper"/>'s row.
    /// </summary>
    public long? PositionLimit { get; set; }

    /// <summary>
    /// Cap on the new order's notional (<c>price × quantity</c>) for
    /// sub-account-tagged Limit orders. Market orders skip — there
    /// is no price to evaluate, exactly like
    /// <see cref="RiskLimits.MaxNotional"/>.
    /// </summary>
    public decimal? MaxNotional { get; set; }
}
