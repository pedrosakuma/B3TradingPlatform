namespace B3.Trading.Application.MarketData;

/// <summary>
/// #454 Fase 1. Per-symbol tick-size lookup. Today the only canonical
/// source is operator config (<see cref="SymbolDirectory"/> →
/// <see cref="InstrumentSpec.ResolveTick(decimal)"/>); Fase 2 will add
/// an SDK-backed impl in <c>B3.Trading.Host</c> that subscribes to the
/// upstream <c>SecurityDefinition</c> event proposed in
/// <c>pedrosakuma/B3MarketDataPlatform#55</c> and falls back to the
/// config-backed impl for bootstrap / operational overrides.
///
/// <para>
/// Consumers MUST treat a <c>false</c> return as
/// "fail-open / approve" — the same posture that
/// <see cref="Risk.Checks.MinTickSizeCheck"/> uses when a symbol is
/// missing from the directory. The default <c>0.01m</c> BRL-equity
/// floor that used to live in <c>AlgoEndpoints</c> is intentionally
/// gone: a missing tick now surfaces as an explicit reject with a
/// precise reason instead of a silent fallback that might mismatch
/// the venue tick and trigger downstream rejects.
/// </para>
///
/// <para>
/// <paramref name="referencePrice"/> is required for CVM-style tiered
/// tick ladders (different ticks per price band — see #360). When the
/// caller has no reference price (e.g. a market order, or a pegged
/// algo with no limit), pass <c>null</c> and the provider falls back
/// to the flat <c>TickSize</c>; symbols configured ONLY with a ladder
/// will return <c>false</c>, forcing the caller to either supply a
/// reference price or reject.
/// </para>
/// </summary>
public interface ITickSizeProvider
{
    bool TryGetTickSize(string symbol, decimal? referencePrice, out decimal tickSize);
}
