using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// WebSocket fan-out for <see cref="IAlgoEventSink"/>. Re-fetches the
/// algo from <see cref="AlgoBook"/> at publish time so subscribers see
/// the latest committed state; the early-return on "no subscribers" is
/// the same micro-optimisation used by <see cref="WebSocketExecutionEventSink"/>.
/// </summary>
public sealed class WebSocketAlgoEventSink : IAlgoEventSink
{
    private readonly SubscriptionManager _subs;
    private readonly AlgoBook _algos;

    public WebSocketAlgoEventSink(SubscriptionManager subs, AlgoBook algos)
    {
        _subs = subs;
        _algos = algos;
    }

    public void PublishAlgoSnapshot(EndClientId owner, string firmId, ulong algoId)
    {
        if (_subs.CountFor(owner) == 0)
            return;
        if (!_algos.TryGet(firmId, algoId, out var algo) || algo is null)
            return;
        _subs.Publish(owner, firmId, Channels.AlgoMe, algo.ToDto());
    }
}
