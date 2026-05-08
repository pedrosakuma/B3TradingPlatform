using B3.Trading.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Production-safety unit coverage for <see cref="ErInjectionBootGuard"/>.
/// Replaces the legacy <c>SimulatorBootGuardTests</c> after #163 merged
/// <c>Mode=Simulator</c> into <c>Mode=Mock + AllowErInjection</c>. The
/// guard is a pure static so we exercise every (env, allow, opt-out)
/// combination without spinning up the host.
/// </summary>
public class ErInjectionBootGuardTests
{
    [Fact]
    public void Validate_Development_AllowErInjection_True_DoesNotThrow()
    {
        ErInjectionBootGuard.Validate("Development", allowErInjection: true, allowInProduction: false);
    }

    [Fact]
    public void Validate_Production_AllowErInjection_True_NoOptOut_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ErInjectionBootGuard.Validate(Environments.Production, allowErInjection: true, allowInProduction: false));
        Assert.Contains("AllowErInjectionInProduction", ex.Message);
    }

    [Fact]
    public void Validate_Production_AllowErInjection_True_WithOptOut_DoesNotThrow()
    {
        ErInjectionBootGuard.Validate(Environments.Production, allowErInjection: true, allowInProduction: true);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Validate_AllowErInjection_False_NeverThrows(string env)
    {
        ErInjectionBootGuard.Validate(env, allowErInjection: false, allowInProduction: false);
    }

    [Fact]
    public void BuildWarning_AllowErInjection_False_ReturnsNull()
    {
        Assert.Null(ErInjectionBootGuard.BuildWarning("Development", allowErInjection: false, allowInProduction: false));
        Assert.Null(ErInjectionBootGuard.BuildWarning("Production", allowErInjection: false, allowInProduction: true));
    }

    [Fact]
    public void BuildWarning_Development_AllowErInjection_True_NotProductionNote()
    {
        var msg = ErInjectionBootGuard.BuildWarning("Development", allowErInjection: true, allowInProduction: false);
        Assert.NotNull(msg);
        Assert.Contains("ER INJECTION ENABLED", msg);
        Assert.Contains("NEVER USE IN PRODUCTION", msg!);
        Assert.DoesNotContain("opt-out is ACTIVE", msg);
    }

    [Fact]
    public void BuildWarning_Production_OptOutActive_AddsActiveNote()
    {
        var msg = ErInjectionBootGuard.BuildWarning(Environments.Production, allowErInjection: true, allowInProduction: true);
        Assert.NotNull(msg);
        Assert.Contains("opt-out is ACTIVE", msg!);
    }
}
