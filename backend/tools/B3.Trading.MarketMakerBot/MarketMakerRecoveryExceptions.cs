using B3.EntryPoint.Client;

namespace B3.Trading.MarketMakerBot;

internal sealed class MarketMakerSessionTerminatedException : IOException
{
    public MarketMakerSessionTerminatedException(TerminationCode code, string? reason)
        : base($"FIXP session terminated by matching: code={code} reason={reason ?? "none"}.")
    {
    }
}

internal sealed class MarketMakerReconciliationRequiredException : InvalidOperationException
{
    public MarketMakerReconciliationRequiredException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
