namespace B3.Trading.Application.MarketData;

/// <summary>
/// #454 Fase 1. Default <see cref="ITickSizeProvider"/>: resolves
/// tick from the operator-configured <see cref="SymbolDirectory"/>
/// (<see cref="InstrumentSpec.TickSize"/> for flat symbols,
/// <see cref="InstrumentSpec.ResolveTick(decimal)"/> for CVM-style
/// tiered ladders). Will be wrapped (NOT replaced) by an SDK-backed
/// impl in <c>B3.Trading.Host</c> in Fase 2, with this impl acting
/// as the bootstrap + operational-override fallback.
/// </summary>
public sealed class SymbolDirectoryTickSizeProvider : ITickSizeProvider
{
    private readonly SymbolDirectory _directory;

    public SymbolDirectoryTickSizeProvider(SymbolDirectory directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public bool TryGetTickSize(string symbol, decimal? referencePrice, out decimal tickSize)
    {
        tickSize = 0m;
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (!_directory.TryGetSpec(symbol, out var spec)) return false;

        // Prefer the ladder when a reference price is available — that's
        // the only way to honor CVM tiered ticks accurately. Without a
        // price, fall back to the flat TickSize; ladder-only symbols
        // legitimately return false here (caller must supply a price
        // or reject — see ITickSizeProvider doc).
        if (referencePrice is { } px && spec.ResolveTick(px) is { } tFromLadder && tFromLadder > 0m)
        {
            tickSize = tFromLadder;
            return true;
        }

        if (spec.TickSize is { } flat && flat > 0m)
        {
            tickSize = flat;
            return true;
        }

        return false;
    }
}
