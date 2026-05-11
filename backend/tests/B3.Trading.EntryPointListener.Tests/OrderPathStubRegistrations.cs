using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.EntryPointListener.Tests;

/// <summary>
/// Shared helper for handshake/transport/retransmit tests that enable
/// the FIXP listener but do not exercise NewOrderSingle / OrderCancel
/// dispatch. Issue #185 introduced
/// <see cref="EntryPointListenerCompositionGuard"/> which fails host
/// startup if the order-path dependencies are not registered; these
/// tests don't need the heavy real services, so we register no-op
/// factory placeholders. The factories are never invoked because the
/// tests never send order frames; <see cref="IServiceProviderIsService"/>
/// reports them as registered which is all the guard requires.
///
/// <para>
/// Note: <see cref="IUserBotOrderMappingRegistry"/> is auto-registered by
/// <c>AddEntryPointListener</c> itself, so it is not stubbed here.
/// </para>
/// </summary>
internal static class OrderPathStubRegistrations
{
    public static IServiceCollection AddNoopOrderPathStubs(this IServiceCollection services)
    {
        // Return null!: AddEntryPointListener uses ActivatorUtilities to
        // build FixpListenerHostedService, which probes DI for these types
        // even though the constructor parameters carry default values of
        // null. By registering null-returning factories we satisfy
        // EntryPointListenerCompositionGuard.IsService() while keeping
        // the hosted service's internal ``_orders`` field null — i.e. the
        // adapter is not constructed, which is exactly what handshake-only
        // tests want. If a test ever sends a NewOrderSingle frame on this
        // wiring the listener now throws InvalidOperationException
        // (issue #185) instead of swallowing it.
        services.AddSingleton<SymbolDirectory>(_ => null!);
        services.AddSingleton<OrderSubmissionService>(_ => null!);
        services.AddSingleton<OrderCancelService>(_ => null!);
        return services;
    }
}
