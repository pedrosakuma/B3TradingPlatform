using B3.Trading.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Pure-function coverage for the simulator boot safeguards (refuse-to-boot
/// + warning text). The Program.cs wire-up just calls
/// <see cref="SimulatorBootGuard.Validate"/> + logs the
/// <see cref="SimulatorBootGuard.BuildWarning"/> string, so testing the
/// guard directly is enough to lock the safeguards in.
/// </summary>
public class SimulatorBootGuardTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Docker")]
    public void NonProduction_AllowsBoot(string env)
    {
        // Default opt-out flag (false). Should not throw.
        SimulatorBootGuard.Validate(env, ExchangeMode.Simulator, allowInProduction: false);
    }

    [Fact]
    public void Production_WithoutOptIn_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SimulatorBootGuard.Validate(Environments.Production, ExchangeMode.Simulator, allowInProduction: false));
        Assert.Contains("AllowSimulatorInProduction", ex.Message);
    }

    [Fact]
    public void Production_WithOptIn_AllowsBoot()
    {
        SimulatorBootGuard.Validate(Environments.Production, ExchangeMode.Simulator, allowInProduction: true);
    }

    [Theory]
    [InlineData(ExchangeMode.Stub)]
    [InlineData(ExchangeMode.Mock)]
    [InlineData(ExchangeMode.Real)]
    [InlineData(ExchangeMode.Unavailable)]
    public void NonSimulator_Modes_AreNeverGuarded(ExchangeMode mode)
    {
        // Even in Production with the opt-out off, non-Simulator modes
        // must boot — the guard is scoped strictly to Simulator.
        SimulatorBootGuard.Validate(Environments.Production, mode, allowInProduction: false);
    }

    [Fact]
    public void Warning_IsNull_WhenNotSimulator()
    {
        Assert.Null(SimulatorBootGuard.BuildWarning("Development", ExchangeMode.Mock, allowInProduction: false));
        Assert.Null(SimulatorBootGuard.BuildWarning("Production", ExchangeMode.Real, allowInProduction: false));
    }

    [Fact]
    public void Warning_NonProduction_HasNoOptOutNote()
    {
        var msg = SimulatorBootGuard.BuildWarning("Development", ExchangeMode.Simulator, allowInProduction: false);
        Assert.NotNull(msg);
        Assert.Contains("SIMULATOR", msg);
        Assert.DoesNotContain("opt-out is ACTIVE", msg);
    }

    [Fact]
    public void Warning_Production_WithOptIn_FlagsActiveOptOut()
    {
        var msg = SimulatorBootGuard.BuildWarning(Environments.Production, ExchangeMode.Simulator, allowInProduction: true);
        Assert.NotNull(msg);
        Assert.Contains("opt-out is ACTIVE", msg);
    }
}
