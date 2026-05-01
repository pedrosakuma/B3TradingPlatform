using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Sink for execution events that fan out to subscribed end-clients
/// (Phase 2 will plug a real WebSocket-backed implementation; until then
/// the default is <see cref="NoOpExecutionEventSink"/>).
/// </summary>
public interface IExecutionEventSink
{
    void Publish(ExecutionEvent ev);
}

/// <summary>
/// Per-end-client view of an ExecutionReport, normalized to the domain.
/// Carries everything Phase 2 needs to render the event in <c>orders.me</c>
/// + <c>executions.me</c> + <c>positions.me</c> channels.
/// </summary>
public sealed record ExecutionEvent(
    EndClientId Owner,
    string ClOrdId,
    string Symbol,
    OrderStatus Status,
    long LeavesQuantity,
    long CumulativeQuantity,
    long LastQuantity,
    decimal LastPrice,
    string? RejectReason);

public sealed class NoOpExecutionEventSink : IExecutionEventSink
{
    public void Publish(ExecutionEvent ev)
    {
    }
}
