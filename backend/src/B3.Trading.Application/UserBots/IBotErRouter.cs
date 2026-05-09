using B3.Trading.Domain;

namespace B3.Trading.Application.UserBots;

/// <summary>
/// Sub-issue #172 (F). Optional hook into the ER pipeline that gives the
/// FIXP outbound multiplexer a chance to forward each
/// <see cref="ExecutionEvent"/> to the originating bot's session. The
/// router is a separate seam from <see cref="IExecutionEventSink"/> so:
/// <list type="bullet">
/// <item>The WS sink and the bot multiplexer are independently wired —
/// REST/WS-only deployments do not pay for the FIXP listener
/// machinery, and the listener can be enabled without disturbing the
/// existing sink registration chain.</item>
/// <item>The router is invoked AFTER the WS publish so subscribers
/// see the event in the same world (RFC §4.7 ordering).</item>
/// </list>
///
/// <para><b>Concurrency contract:</b> implementations MUST NOT do
/// async I/O on this call — it runs on the ER processor thread, which
/// in turn runs inside an <c>EventDispatcher</c> apply callback under
/// the dispatcher lock. The standard implementation enqueues to an
/// internal channel drained by a background worker.</para>
/// </summary>
public interface IBotErRouter
{
    void Route(ExecutionEvent ev);
}

/// <summary>
/// Default no-op router used when the FIXP listener is disabled
/// (REST/WS-only deployments). Keeps <see cref="ExecutionReportProcessor"/>
/// agnostic of whether the multiplexer is wired.
/// </summary>
public sealed class NoOpBotErRouter : IBotErRouter
{
    public void Route(ExecutionEvent ev) { }
}
