using Microsoft.Extensions.Hosting;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Production safeguard for synthetic ER injection (formerly the
/// <c>SimulatorBootGuard</c> that gated <c>ExchangeMode.Simulator</c>;
/// renamed in #163 when Simulator was merged into <c>Mock</c> +
/// <see cref="ExchangeOptions.AllowErInjection"/>). Synthetic ER
/// injection has catastrophic blast radius if it leaks into a
/// real-money deployment, so the host refuses to boot when Production
/// + <c>AllowErInjection=true</c> unless the operator has explicitly
/// opted in via <see cref="ExchangeOptions.AllowErInjectionInProduction"/>.
/// Extracted as a pure static so unit tests can exercise every branch
/// without spinning up the host.
/// </summary>
public static class ErInjectionBootGuard
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when ER injection is
    /// active in Production without the explicit opt-out flag. No-op for
    /// every other (env, flag) combination.
    /// </summary>
    public static void Validate(string environmentName, bool allowErInjection, bool allowInProduction)
    {
        if (!allowErInjection) return;
        var isProduction = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        if (isProduction && !allowInProduction)
        {
            throw new InvalidOperationException(
                "Trading:Exchange:AllowErInjection=true is not allowed in Production. " +
                "Set Trading:Exchange:AllowErInjectionInProduction=true to opt in (synthetic ER injection has catastrophic blast radius — only enable for production-shaped sandboxes with no real-money risk).");
        }
    }

    /// <summary>
    /// Builds the boot-time warning message. Always emitted at Warning
    /// level when ER injection is active. Returns null when not active so
    /// the caller can simply skip logging.
    /// </summary>
    public static string? BuildWarning(string environmentName, bool allowErInjection, bool allowInProduction)
    {
        if (!allowErInjection) return null;
        var isProduction = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        var prodNote = isProduction && allowInProduction
            ? " AllowErInjectionInProduction=true is currently set — opt-out is ACTIVE."
            : string.Empty;
        return "⚠ ER INJECTION ENABLED — POST /admin/simulator/er accepts synthetic execution reports for any working ClOrdId. " +
            "Test/dev only. NEVER USE IN PRODUCTION." + prodNote;
    }
}
