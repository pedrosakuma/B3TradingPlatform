using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.EntryPointListener.Hosting;

internal sealed class LazyBotExecutionFanOutSink(IServiceProvider services) : IExecutionFanOutSink
{
    private BotErMultiplexer? _inner;

    public ExecutionFanOutTargets Target => ExecutionFanOutTargets.BotRouter;

    public void Enqueue(long seq, ExecutionEvent ev)
    {
        _inner ??= services.GetRequiredService<BotErMultiplexer>();
        ((IExecutionFanOutSink)_inner).Enqueue(seq, ev);
    }
}
