using B3.Trading.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #679. Production-safety unit coverage for
/// <see cref="SandboxCashDepositBootGuard"/>. Mirrors
/// <see cref="ErInjectionBootGuard"/>'s test shape — the guard is a pure
/// static so we exercise every (env, allow, opt-out) combination without
/// spinning up the host.
/// </summary>
public class SandboxCashDepositBootGuardTests
{
    [Fact]
    public void Validate_Development_AllowSelfCashDeposit_True_DoesNotThrow()
    {
        SandboxCashDepositBootGuard.Validate("Development", allowSelfCashDeposit: true, allowInProduction: false);
    }

    [Fact]
    public void Validate_Production_AllowSelfCashDeposit_True_NoOptOut_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SandboxCashDepositBootGuard.Validate(Environments.Production, allowSelfCashDeposit: true, allowInProduction: false));
        Assert.Contains("AllowSelfCashDepositInProduction", ex.Message);
    }

    [Fact]
    public void Validate_Production_AllowSelfCashDeposit_True_WithOptOut_DoesNotThrow()
    {
        SandboxCashDepositBootGuard.Validate(Environments.Production, allowSelfCashDeposit: true, allowInProduction: true);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Validate_AllowSelfCashDeposit_False_NeverThrows(string env)
    {
        SandboxCashDepositBootGuard.Validate(env, allowSelfCashDeposit: false, allowInProduction: false);
    }

    [Fact]
    public void BuildWarning_AllowSelfCashDeposit_False_ReturnsNull()
    {
        Assert.Null(SandboxCashDepositBootGuard.BuildWarning("Development", allowSelfCashDeposit: false, allowInProduction: false));
        Assert.Null(SandboxCashDepositBootGuard.BuildWarning("Production", allowSelfCashDeposit: false, allowInProduction: true));
    }

    [Fact]
    public void BuildWarning_Development_AllowSelfCashDeposit_True_NotProductionNote()
    {
        var msg = SandboxCashDepositBootGuard.BuildWarning("Development", allowSelfCashDeposit: true, allowInProduction: false);
        Assert.NotNull(msg);
        Assert.Contains("SELF-SERVICE CASH DEPOSIT ENABLED", msg);
        Assert.Contains("NEVER USE IN PRODUCTION", msg!);
        Assert.DoesNotContain("opt-out is ACTIVE", msg);
    }

    [Fact]
    public void BuildWarning_Production_OptOutActive_AddsActiveNote()
    {
        var msg = SandboxCashDepositBootGuard.BuildWarning(Environments.Production, allowSelfCashDeposit: true, allowInProduction: true);
        Assert.NotNull(msg);
        Assert.Contains("opt-out is ACTIVE", msg!);
    }
}
