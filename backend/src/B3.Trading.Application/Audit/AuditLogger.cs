using System.Diagnostics;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.Audit;

/// <summary>
/// Q4.5 (#305). Pipeline-uniform helper for emitting an
/// <see cref="AuditLogEvent"/>. Every capture site funnels through
/// this so the WAL append + keeper fold + metric increment happen in
/// the same order, under the same dispatcher lock, regardless of
/// where the call site lives (Auth, TOTP, AdminEndpoints,
/// SubAccountsEndpoints, AdminFixp).
///
/// <para><b>Failure posture (RFC §4.4).</b> Audit is best-effort by
/// design — a WAL backpressure exception while logging a failed
/// login MUST NOT mask the underlying login failure to the caller.
/// <see cref="Log"/> swallows <see cref="WalBackpressureException"/>
/// (after counting it) and any unexpected exception (after
/// logging at <c>Error</c>); the caller's own structured response
/// to the user is unaffected. Positions-style fail-closed is the
/// wrong default here — the audit log is forensic, not
/// transactional.</para>
/// </summary>
public interface IAuditLogger
{
    void Log(AuditLogEvent evt);
}

/// <summary>Concrete <see cref="IAuditLogger"/> backed by the dispatcher + keeper.</summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly EventDispatcher _dispatcher;
    private readonly AuditLogKeeper _keeper;
    private readonly ILogger<AuditLogger> _log;

    public AuditLogger(EventDispatcher dispatcher, AuditLogKeeper keeper, ILogger<AuditLogger> log)
    {
        _dispatcher = dispatcher;
        _keeper = keeper;
        _log = log;
    }

    public void Log(AuditLogEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        try
        {
            // Dispatch under the lock so the WAL seq assigned by Append
            // is the same seq the keeper records. Reading CurrentSeq
            // post-dispatch would race against another dispatcher tick.
            _dispatcher.Dispatch(evt, () =>
            {
                // The dispatcher's apply() runs under the lock with the
                // current seq already assigned to evt. CurrentSeq reads
                // a different lock-guarded field, so it is safe here.
                _keeper.Apply(_dispatcher.CurrentSeq, evt);
            });
        }
        catch (WalBackpressureException ex)
        {
            // Audit is best-effort — never let the writer's backpressure
            // mask the underlying business outcome to the caller. The
            // WAL backpressure counter already classifies these.
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "audit.log"));
            _log.LogWarning(ex, "Audit append dropped under WAL backpressure: type={EventType}", evt.EventType);
        }
        catch (Exception ex)
        {
            // Defensive: keepers / serialisers should never throw, but
            // if they do we still must not poison the caller.
            _log.LogError(ex, "Audit append failed: type={EventType}", evt.EventType);
        }

        // Steady-state metric. Always incremented (success or swallowed
        // failure) so the operator sees the attempted-emit rate; the
        // WAL counter above gives the dropped-by-backpressure subset.
        MetricsRegistry.AuditEventsTotal.Add(1,
            new KeyValuePair<string, object?>("event_type", evt.EventType),
            new KeyValuePair<string, object?>("outcome", evt.Outcome));

        Debug.Assert(evt.EventType.Length > 0, "EventType must not be empty");
    }
}

/// <summary>
/// Q4.5 (#305). No-op <see cref="IAuditLogger"/> for tests that
/// don't wire the WAL + keeper. The default composition uses
/// <see cref="AuditLogger"/>; <c>NullAuditLogger</c> exists so unit
/// tests of capture sites don't need a full host.
/// </summary>
public sealed class NullAuditLogger : IAuditLogger
{
    public void Log(AuditLogEvent evt) { }
}
