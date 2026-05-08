using Microsoft.Extensions.Hosting;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Production safeguard for <see cref="ExchangeMode.Simulator"/> (RFC
/// algo-orders-v0 §4.10/§7-B3). Synthetic ER injection has catastrophic
/// blast radius if it leaks into a real-money deployment, so the host
/// refuses to boot when Production + Simulator unless the operator has
/// explicitly opted in via <see cref="ExchangeOptions.AllowSimulatorInProduction"/>.
/// Extracted as a pure static so unit tests can exercise every branch
/// without spinning up the host.
/// </summary>
public static class SimulatorBootGuard
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when Simulator is
    /// active in Production without the explicit opt-out flag. No-op for
    /// every other (mode, environment) combination.
    /// </summary>
    public static void Validate(string environmentName, ExchangeMode mode, bool allowInProduction)
    {
        if (mode != ExchangeMode.Simulator) return;
        var isProduction = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        if (isProduction && !allowInProduction)
        {
            throw new InvalidOperationException(
                "Trading:Exchange:Mode=Simulator is not allowed in Production. " +
                "Set Trading:Exchange:AllowSimulatorInProduction=true to opt in (synthetic ER injection has catastrophic blast radius — only enable for production-shaped sandboxes with no real-money risk).");
        }
    }

    /// <summary>
    /// Builds the boot-time warning message. Always emitted at Warning
    /// level when Simulator is active. Returns null when Simulator is not
    /// active so the caller can simply skip logging.
    /// </summary>
    public static string? BuildWarning(string environmentName, ExchangeMode mode, bool allowInProduction)
    {
        if (mode != ExchangeMode.Simulator) return null;
        var isProduction = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        var prodNote = isProduction && allowInProduction
            ? " AllowSimulatorInProduction=true is currently set — opt-out is ACTIVE."
            : string.Empty;
        // #134: this in-process synthetic-ER path is on the deprecation
        // ramp; the external SimulatorBot (docker-compose.simulator-bot.yml)
        // is the supported replacement and goes through real FIXP.
        return $"⚠ EXCHANGE MODE: SIMULATOR — synthetic ER injection enabled. " +
            $"DEPRECATED (#134): use the external SimulatorBot via " +
            $"docker/docker-compose.simulator-bot.yml instead. NEVER USE IN PRODUCTION.{prodNote}";
    }
}
