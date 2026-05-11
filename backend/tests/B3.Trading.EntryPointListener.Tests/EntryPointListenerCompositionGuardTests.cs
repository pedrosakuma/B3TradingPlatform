using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Tests;

/// <summary>
/// Issue #185 — regression tests for the listener composition guard.
///
/// <para>
/// Negative path: enabling the listener while leaving any order-path
/// dependency unregistered must abort host startup with a clear,
/// actionable <see cref="InvalidOperationException"/> rather than
/// completing the handshake and silently swallowing inbound order
/// frames. We exercise each missing dependency in isolation.
/// </para>
///
/// <para>
/// Pure unit-level coverage of <see cref="EntryPointListenerCompositionGuard"/>
/// without spinning up the hosted service is included to make the
/// failure message contract explicit.
/// </para>
/// </summary>
public class EntryPointListenerCompositionGuardTests
{
    // ─── Pure unit tests on the guard ─────────────────────────────────

    [Fact]
    public void Validate_Disabled_NoOp_EvenWithEmptyContainer()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var opts = new EntryPointListenerOptions { Enabled = false };

        // Should not throw despite container being empty.
        EntryPointListenerCompositionGuard.Validate(sp, opts);
    }

    [Fact]
    public void Validate_Enabled_AllRegistered_DoesNotThrow()
    {
        var sp = BuildServiceProviderWithAllOrderPathDeps();
        var opts = new EntryPointListenerOptions { Enabled = true };

        EntryPointListenerCompositionGuard.Validate(sp, opts);
    }

    [Theory]
    [InlineData(typeof(SymbolDirectory), "SymbolDirectory")]
    [InlineData(typeof(OrderSubmissionService), "OrderSubmissionService")]
    [InlineData(typeof(OrderCancelService), "OrderCancelService")]
    [InlineData(typeof(IUserBotOrderMappingRegistry), "IUserBotOrderMappingRegistry")]
    public void Validate_Enabled_MissingOneDep_ThrowsAndNamesIt(Type missing, string expectedNameSubstring)
    {
        var services = new ServiceCollection();
        // Register everything except the one we want missing.
        if (missing != typeof(SymbolDirectory))
            services.AddSingleton<SymbolDirectory>(_ => null!);
        if (missing != typeof(OrderSubmissionService))
            services.AddSingleton<OrderSubmissionService>(_ => null!);
        if (missing != typeof(OrderCancelService))
            services.AddSingleton<OrderCancelService>(_ => null!);
        if (missing != typeof(IUserBotOrderMappingRegistry))
            services.AddSingleton<IUserBotOrderMappingRegistry>(_ => null!);

        var sp = services.BuildServiceProvider();
        var opts = new EntryPointListenerOptions { Enabled = true };

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntryPointListenerCompositionGuard.Validate(sp, opts));

        Assert.Contains(expectedNameSubstring, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Trading:EntryPointListener:Enabled=true", ex.Message, StringComparison.Ordinal);
        Assert.Contains("silently ignore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Enabled_AllDepsMissing_ListsEveryOneInMessage()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var opts = new EntryPointListenerOptions { Enabled = true };

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntryPointListenerCompositionGuard.Validate(sp, opts));

        Assert.Contains("SymbolDirectory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OrderSubmissionService", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OrderCancelService", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IUserBotOrderMappingRegistry", ex.Message, StringComparison.Ordinal);
    }

    // ─── End-to-end host startup coverage ─────────────────────────────

    [Fact]
    public async Task Host_ListenerEnabled_OrderSubmissionServiceMissing_StartAsyncThrows()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:EntryPointListener:Enabled"] = "true",
                ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
            })
            .Build();

        using var host = new HostBuilder()
            .ConfigureServices((_, s) =>
            {
                s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                s.AddSingleton<InMemoryUserBotCredentialRegistry>();
                s.AddSingleton<IUserBotCredentialRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotCredentialRegistry>());
                s.AddSingleton<InMemoryUserBotSessionRegistry>();
                s.AddSingleton<IUserBotSessionRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotSessionRegistry>());

                // Intentionally omit OrderSubmissionService to simulate
                // the regression from issue #185.
                s.AddSingleton<SymbolDirectory>(_ => null!);
                s.AddSingleton<OrderCancelService>(_ => null!);
                // IUserBotOrderMappingRegistry is auto-registered by AddEntryPointListener.

                s.AddEntryPointListener(config);
            })
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(CancellationToken.None));
        Assert.Contains("OrderSubmissionService", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Trading:EntryPointListener:Enabled=true", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_ListenerDisabled_GuardNotRegistered_StartsCleanly()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:EntryPointListener:Enabled"] = "false",
            })
            .Build();

        using var host = new HostBuilder()
            .ConfigureServices((_, s) =>
            {
                s.AddLogging();
                s.AddEntryPointListener(config);
            })
            .Build();

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
    }

    private static IServiceProvider BuildServiceProviderWithAllOrderPathDeps()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SymbolDirectory>(_ => null!);
        services.AddSingleton<OrderSubmissionService>(_ => null!);
        services.AddSingleton<OrderCancelService>(_ => null!);
        services.AddSingleton<IUserBotOrderMappingRegistry>(_ => null!);
        return services.BuildServiceProvider();
    }
}
