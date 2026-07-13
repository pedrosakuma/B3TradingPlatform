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
/// <para><b>Two failure postures.</b> Audit capture lives on the
/// availability/forensics boundary, so the logger exposes two
/// explicit modes — picking the right one is the call site's
/// contract:
/// <list type="bullet">
///   <item><see cref="Log"/> — <i>best-effort</i>. Swallows
///   <see cref="WalBackpressureException"/> (after counting it) and
///   any unexpected exception (after logging at <c>Error</c>). Use
///   for high-frequency, attacker-influenced paths where audit
///   loss is preferable to amplifying a DoS against the WAL —
///   notably <c>auth.login.success</c>/<c>auth.login.failure</c>
///   and the TOTP verify paths. Pass-1 review (#322) P1.2 — the
///   contract here is that the caller's structured response is
///   unaffected by audit failures. Every drop bumps
///   <c>trading.audit.dropped_total{call_site,event_type,reason}</c>
///   (#438) — operators MUST alert on sustained non-zero rate so
///   the deliberate availability/forensics trade-off does not
///   silently hide audit loss.</item>
///   <item><see cref="LogOrFail"/> — <i>fail-closed for the
///   WAL</i>. Propagates <see cref="WalBackpressureException"/> to
///   the caller so admin endpoints can translate it into HTTP 503
///   (with the call-site WAL backpressure counter incremented
///   exactly once at the audit site). Other unexpected exceptions
///   are still swallowed-and-logged to avoid bricking the endpoint
///   on a defect in the keeper. Use for security-sensitive
///   <c>/admin/*</c> mutations where the audit-first ordering
///   (audit append accepted by the WAL → business event
///   dispatched) means a successful response implies a durable
///   audit record exists; if audit cannot be captured we MUST
///   refuse the mutation rather than silently un-audit it.</item>
/// </list></para>
///
/// <para><b>Ordering contract for admin mutations.</b> Admin
/// mutating endpoints invoke <see cref="LogOrFail"/> BEFORE
/// dispatching the business <see cref="WalEvent"/> for the same
/// request. The audit envelope therefore records the operator's
/// <i>attempt</i> — if the subsequent business dispatch crashes,
/// is backpressured, or is rejected by a post-validation gate, the
/// audit trail still shows the intent (which is what auditors
/// want). Pre-validation that yields a deterministic denial (input
/// shape, missing body) runs first and emits a single audit with
/// the appropriate <c>denied</c> outcome via
/// <see cref="LogOrFail"/>.</para>
/// </summary>
public interface IAuditLogger
{
    /// <summary>Best-effort audit append. Swallows WAL backpressure and unexpected exceptions; see the interface remarks for when to choose this mode.</summary>
    void Log(AuditLogEvent evt);

    /// <summary>
    /// Fail-closed audit append for security-sensitive admin
    /// mutations. Propagates <see cref="WalBackpressureException"/>
    /// to the caller (admin endpoints translate to HTTP 503) so the
    /// caller can refuse the business action rather than silently
    /// un-audit it. Other unexpected exceptions are swallowed (the
    /// keeper/dispatcher contract is that they never throw — see
    /// interface remarks). See the interface remarks for the
    /// audit-first ordering contract.
    /// </summary>
    void LogOrFail(AuditLogEvent evt);
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
            DispatchUnderLock(evt);
        }
        catch (WalBackpressureException ex)
        {
            // Audit is best-effort — never let the writer's backpressure
            // mask the underlying business outcome to the caller. The
            // WAL backpressure counter already classifies these.
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "audit.log"));
            RecordDropMetric(evt, reason: "wal_backpressure");
            _log.LogWarning(ex, "Audit append dropped under WAL backpressure: type={EventType}", evt.EventType);
        }
        catch (Exception ex)
        {
            // Defensive: keepers / serialisers should never throw, but
            // if they do we still must not poison the caller.
            RecordDropMetric(evt, reason: "exception");
            _log.LogError(ex, "Audit append failed: type={EventType}", evt.EventType);
        }

        RecordEmitMetric(evt);
    }

    public void LogOrFail(AuditLogEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        try
        {
            DispatchUnderLock(evt);
        }
        catch (WalBackpressureException)
        {
            // Pass-1 review (#322) P1.2. Admin mutating endpoints rely
            // on this exception bubbling up so they can refuse the
            // business mutation with HTTP 503; swallowing it here would
            // re-introduce the fail-open audit gap. Count it at the
            // audit call site so the operator's WAL backpressure
            // dashboard separates audit-driven backpressure from
            // business-event-driven backpressure.
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "audit.log_or_fail"));
            RecordDropMetric(evt, reason: "wal_backpressure");
            // We still account the emit attempt so the audit-throughput
            // metric reflects offered load. Outcome on the event is
            // whatever the call site chose (typically "success" for the
            // intent record; denial paths emit with "denied").
            RecordEmitMetric(evt);
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: the keeper/dispatcher contract is that these
            // never throw. If they do (genuine bug), we still must not
            // brick the endpoint — fall through to best-effort.
            RecordDropMetric(evt, reason: "exception");
            _log.LogError(ex, "Audit append failed (LogOrFail path): type={EventType}", evt.EventType);
        }

        RecordEmitMetric(evt);
    }

    private void DispatchUnderLock(AuditLogEvent evt)
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

    private static void RecordEmitMetric(AuditLogEvent evt)
    {
        // Steady-state metric. Always incremented (success or swallowed
        // failure) so the operator sees the attempted-emit rate; the
        // WAL counter above gives the dropped-by-backpressure subset.
        MetricsRegistry.AuditEventsTotal.Add(1,
            new KeyValuePair<string, object?>("event_type", evt.EventType),
            new KeyValuePair<string, object?>("outcome", evt.Outcome));

        Debug.Assert(evt.EventType.Length > 0, "EventType must not be empty");
    }

    /// <summary>
    /// #438. First-class drop metric so operator alerts can target
    /// "audit lost" independently of the broader WAL backpressure
    /// counter. <paramref name="reason"/> is one of
    /// <c>wal_backpressure</c> | <c>exception</c>. <c>call_site</c>
    /// is derived from the first segment of the canonical event type
    /// (<c>auth</c>, <c>admin</c>, <c>totp</c>, …) so cardinality
    /// stays bounded.
    /// </summary>
    private static void RecordDropMetric(AuditLogEvent evt, string reason)
    {
        MetricsRegistry.AuditDropped.Add(1,
            new KeyValuePair<string, object?>("call_site", DeriveCallSite(evt.EventType)),
            new KeyValuePair<string, object?>("event_type", evt.EventType),
            new KeyValuePair<string, object?>("reason", reason));
    }

    private static string DeriveCallSite(string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return "unknown";
        var dot = eventType.IndexOf('.');
        return dot <= 0 ? eventType : eventType[..dot];
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
    public void LogOrFail(AuditLogEvent evt) { }
}
