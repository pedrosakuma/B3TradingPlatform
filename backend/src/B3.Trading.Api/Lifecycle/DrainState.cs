using System.Diagnostics;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Observability;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.Lifecycle;

/// <summary>
/// Process-wide drain flag. Flipped on <c>SIGTERM</c> /
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
///
/// Once draining:
/// <list type="bullet">
///   <item><c>/ready</c> returns 503 so an upstream LB stops sending traffic.</item>
///   <item><c>POST /api/orders</c> returns 503 — no new orders accepted.</item>
///   <item>In-flight requests complete; ER router and WAL keep running so
///   replies for already-submitted orders flush out.</item>
/// </list>
/// <c>/live</c> stays 200 throughout — the process is still healthy,
/// just refusing new work.
/// </summary>
public sealed class DrainState : IDrainController
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly HashSet<string> _reasons = new(StringComparer.Ordinal);
    private long _draining; // 0 = serving, 1 = draining
    private string? _reason;

    public bool IsDraining => Interlocked.Read(ref _draining) == 1;
    public string? Reason => Volatile.Read(ref _reason);
    public TimeSpan Uptime => _uptime.Elapsed;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void BeginDrain() => BeginDrain("host_stopping");

    public void BeginDrain(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            _reasons.Add(reason);
            if (_reason is null || !IsOutboundReconciliationReason(reason))
                _reason = reason;
            Interlocked.Exchange(ref _draining, 1);
        }
    }

    public bool TryEndOutboundReconciliationDrain()
    {
        lock (_gate)
        {
            var removed =
                _reasons.Remove("outbound_new_order_reconciliation_required") |
                _reasons.Remove("outbound_cancel_replace_reconciliation_required");
            return TryEndDrainUnsafe(removed);
        }
    }

    public bool TryEndColdStartLifecycleIntentsDrain()
    {
        lock (_gate)
        {
            var removed = _reasons.Remove("cold_start_unresolved_lifecycle_intents");
            return TryEndDrainUnsafe(removed);
        }
    }

    private bool TryEndDrainUnsafe(bool removed)
    {
        if (!removed)
            return false;

        if (_reasons.Count == 0)
        {
            _reason = null;
            Interlocked.Exchange(ref _draining, 0);
            return true;
        }

        _reason = _reasons.FirstOrDefault(
            reason => !IsOutboundReconciliationReason(reason))
            ?? _reasons.First();
        return false;
    }

    private static bool IsOutboundReconciliationReason(string reason) =>
        reason is
            "outbound_new_order_reconciliation_required" or
            "outbound_cancel_replace_reconciliation_required" or
            "cold_start_unresolved_lifecycle_intents";
}

/// <summary>
/// Hosted service that wires <see cref="DrainState.BeginDrain"/> to the
/// host's <see cref="IHostApplicationLifetime.ApplicationStopping"/>
/// signal. Registering it as <c>IHostedService</c> is enough — no
/// background loop required.
/// </summary>
internal sealed class DrainHostedService : IHostedService
{
    private readonly DrainState _state;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationTokenRegistration _registration;

    public DrainHostedService(DrainState state, IHostApplicationLifetime lifetime)
    {
        _state = state;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _lifetime.ApplicationStopping.Register(
            () => _state.BeginDrain("host_stopping"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration.Dispose();
        return Task.CompletedTask;
    }
}

internal static class DrainMetrics
{
    public static void RecordRejection(string reason)
    {
        MetricsRegistry.DrainRejections.Add(1,
            new KeyValuePair<string, object?>("reason", reason));
    }
}
