using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.EntryPointListener;

/// <summary>
/// Startup composition guard for the inbound FIXP listener (issue #185).
///
/// <para>
/// The listener accepts the order-path dependencies — <see cref="SymbolDirectory"/>,
/// <see cref="OrderSubmissionService"/>, <see cref="OrderCancelService"/> and
/// <see cref="IUserBotOrderMappingRegistry"/> — through optional constructor
/// arguments so that handshake-only test harnesses can wire the listener
/// without dragging in the full Application graph. In production wiring,
/// however, every one of those services MUST be registered: if any are
/// missing, <see cref="Hosting.FixpSessionConnection"/> would silently swallow
/// inbound <c>NewOrderSingle</c> / <c>OrderCancelRequest</c> frames after a
/// successful Negotiate/Establish — a catastrophic invisible failure mode in
/// real-money traffic.
/// </para>
///
/// <para>
/// This guard, registered eagerly by
/// <see cref="EntryPointListenerServiceCollectionExtensions.AddEntryPointListener"/>
/// when <c>Trading:EntryPointListener:Enabled=true</c>, runs before the
/// hosted listener starts and throws an <see cref="InvalidOperationException"/>
/// listing every missing registration so the operator gets an immediately
/// actionable boot failure instead of a silently broken socket. Mirrors the
/// pure-static, DI-free shape of <see cref="EntryPointListenerBootGuard"/>
/// and <see cref="B3.Trading.Infrastructure.ErInjectionBootGuard"/>.
/// </para>
/// </summary>
public static class EntryPointListenerCompositionGuard
{
    /// <summary>
    /// Names of the order-path dependencies that <see cref="FixpOrderAdapter"/>
    /// composes inside <see cref="Hosting.FixpListenerHostedService"/>. The
    /// list is kept here (and not derived from reflection) so the failure
    /// message remains stable and grep-friendly.
    /// </summary>
    public static IReadOnlyList<(Type Type, string ConfigKeyHint)> RequiredOrderPathDependencies { get; } =
        new (Type, string)[]
        {
            (typeof(SymbolDirectory),
                "B3.Trading.Application.SymbolDirectory (register via AddSingleton<SymbolDirectory>)"),
            (typeof(OrderSubmissionService),
                "B3.Trading.Application.OrderSubmissionService (register via AddSingleton<OrderSubmissionService>)"),
            (typeof(OrderCancelService),
                "B3.Trading.Application.OrderCancelService (register via AddSingleton<OrderCancelService>)"),
            (typeof(IUserBotOrderMappingRegistry),
                "B3.Trading.Application.UserBots.IUserBotOrderMappingRegistry (register via AddSingleton<IUserBotOrderMappingRegistry>)"),
        };

    /// <summary>
    /// Asserts that every order-path dependency is registered in the service
    /// provider when the listener is enabled. Throws
    /// <see cref="InvalidOperationException"/> with all missing registrations
    /// enumerated when one or more are absent. No-op when the listener is
    /// disabled.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IServiceProviderIsService"/> so the check verifies
    /// registration without forcing eager construction of the heavy
    /// Application-layer singletons (which themselves carry deep dep
    /// graphs).
    /// </remarks>
    public static void Validate(IServiceProvider serviceProvider, EntryPointListenerOptions opts)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(opts);
        if (!opts.Enabled) return;

        var probe = serviceProvider.GetService<IServiceProviderIsService>();
        if (probe is null)
        {
            throw new InvalidOperationException(
                "EntryPointListenerCompositionGuard requires IServiceProviderIsService " +
                "(provided by Microsoft.Extensions.DependencyInjection ≥ 6.0). " +
                "Cannot verify FIXP listener order-path wiring; refusing to start.");
        }

        var missing = new List<string>();
        foreach (var (type, hint) in RequiredOrderPathDependencies)
        {
            if (!probe.IsService(type))
                missing.Add(hint);
        }

        if (missing.Count == 0) return;

        var msg =
            "Trading:EntryPointListener:Enabled=true but the FIXP listener order-path " +
            "is incomplete. The following dependencies are not registered in DI:" +
            Environment.NewLine +
            "  - " + string.Join(Environment.NewLine + "  - ", missing) + Environment.NewLine +
            "Without them, FixpSessionConnection would silently ignore inbound " +
            "NewOrderSingle / OrderCancelRequest frames after a successful handshake. " +
            "Either register the missing services or set Trading:EntryPointListener:Enabled=false.";
        throw new InvalidOperationException(msg);
    }
}
