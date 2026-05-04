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
///   <item><c>POST /orders</c> returns 503 — no new orders accepted.</item>
///   <item>In-flight requests complete; ER router and WAL keep running so
///   replies for already-submitted orders flush out.</item>
/// </list>
/// <c>/live</c> stays 200 throughout — the process is still healthy,
/// just refusing new work.
/// </summary>
public sealed class DrainState : IDrainGate
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private long _draining; // 0 = serving, 1 = draining

    public bool IsDraining => Interlocked.Read(ref _draining) == 1;
    public TimeSpan Uptime => _uptime.Elapsed;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void BeginDrain()
    {
        Interlocked.Exchange(ref _draining, 1);
    }
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
        _registration = _lifetime.ApplicationStopping.Register(() => _state.BeginDrain());
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
