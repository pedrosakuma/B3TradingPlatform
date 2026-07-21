namespace B3.Trading.Application;

/// <summary>
/// #679. Config surface for the self-service cash deposit endpoint
/// (<c>POST /balance/deposit</c>). Lets an authenticated end-client top
/// up their own <see cref="CashLedger"/> balance without operator
/// intervention — the sandbox/demo motivator being that buying power
/// (not just a working order) is required for a trade to actually
/// happen, and requiring an admin round-trip for every top-up doesn't
/// scale for self-serve demo accounts.
///
/// <para>
/// Disabled by default (<see cref="AllowSelfCashDeposit"/> = <c>false</c>)
/// so production deployments never expose it by accident; when the
/// endpoint is disabled the route itself is not mapped (404, not 403 —
/// mirrors the <c>POST /admin/simulator/er</c> conditional-mount pattern
/// gated by <c>ExchangeOptions.AllowErInjection</c>).
/// </para>
/// </summary>
public sealed class SandboxCashOptions
{
    public const string SectionName = "Trading:Sandbox";

    /// <summary>
    /// Master switch. When <c>false</c> (default), <c>POST /balance/deposit</c>
    /// is not mapped at all.
    /// </summary>
    public bool AllowSelfCashDeposit { get; set; }

    /// <summary>
    /// Production opt-out for <see cref="AllowSelfCashDeposit"/>. When
    /// <c>false</c> (default), the host refuses to boot if self-deposit is
    /// enabled while <c>Environment=Production</c> — letting any
    /// authenticated end-client mint their own buying power is a
    /// real-money risk outside an explicit sandbox deployment.
    /// </summary>
    public bool AllowSelfCashDepositInProduction { get; set; }

    /// <summary>
    /// Maximum amount accepted in a single <c>POST /balance/deposit</c>
    /// call. Rejected with 422 above this bound. Anti-abuse guardrail —
    /// exact production value still to be defined per #679.
    /// </summary>
    public decimal MaxDepositAmount { get; set; } = 1_000_000m;

    /// <summary>
    /// Maximum resulting <see cref="CashLedger"/> balance a self-deposit
    /// is allowed to reach for the depositing end-client. Rejected with
    /// 422 if the deposit would push the post-mutation balance above this
    /// bound. Guards against unbounded balance growth via repeated small
    /// deposits when <see cref="MaxDepositAmount"/> alone isn't enough.
    /// </summary>
    public decimal MaxBalanceAfterDeposit { get; set; } = 10_000_000m;
}
