using B3.Trading.Application;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Infrastructure;

/// <summary>
/// #679. Production safeguard for <see cref="SandboxCashOptions.AllowSelfCashDeposit"/>,
/// mirroring <see cref="ErInjectionBootGuard"/>. Letting any authenticated
/// end-client mint their own buying power via <c>POST /balance/deposit</c>
/// is a real-money risk if it leaks into a production deployment, so the
/// host refuses to boot when Production + <c>AllowSelfCashDeposit=true</c>
/// unless the operator has explicitly opted in via
/// <see cref="SandboxCashOptions.AllowSelfCashDepositInProduction"/>.
/// Extracted as a pure static so unit tests can exercise every branch
/// without spinning up the host.
/// </summary>
public static class SandboxCashDepositBootGuard
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when self-service
    /// cash deposit is active in Production without the explicit opt-out
    /// flag. No-op for every other (env, flag) combination.
    /// </summary>
    public static void Validate(string environmentName, bool allowSelfCashDeposit, bool allowInProduction)
    {
        if (!allowSelfCashDeposit) return;
        var isProduction = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        if (isProduction && !allowInProduction)
        {
            throw new InvalidOperationException(
                "Trading:Sandbox:AllowSelfCashDeposit=true is not allowed in Production. " +
                "Set Trading:Sandbox:AllowSelfCashDepositInProduction=true to opt in (self-service cash deposit lets any authenticated end-client mint their own buying power — only enable for production-shaped sandboxes with no real-money risk).");
        }
    }

    /// <summary>
    /// Builds the boot-time warning message. Always emitted at Warning
    /// level when self-deposit is active. Returns null when not active so
    /// the caller can simply skip logging.
    /// </summary>
    public static string? BuildWarning(string environmentName, bool allowSelfCashDeposit, bool allowInProduction)
    {
        if (!allowSelfCashDeposit) return null;
        var isProduction = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        var prodNote = isProduction && allowInProduction
            ? " AllowSelfCashDepositInProduction=true is currently set — opt-out is ACTIVE."
            : string.Empty;
        return "⚠ SELF-SERVICE CASH DEPOSIT ENABLED — POST /balance/deposit lets any authenticated end-client top up their own balance. " +
            "Sandbox/demo only. NEVER USE IN PRODUCTION." + prodNote;
    }
}
