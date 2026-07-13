namespace B3.Trading.Application.MarketData;

/// <summary>
/// OPT-B (#484). Per-symbol gross notional calculation. For equity
/// this is the historical <c>price * quantity</c>; for options
/// (<see cref="SecurityType.Option"/>) it multiplies by the contract
/// multiplier landed in OPT-A (#483) — option quantities are in
/// CONTRACTS and each contract is worth <c>multiplier</c> shares of
/// the underlying (typically 100 for B3 equity options).
///
/// <para>
/// Without this seam, every pre-trade notional gate
/// (<c>MaxNotionalCheck</c>, <c>MinNotionalCheck</c>,
/// <c>SubAccountLimitsCheck.MaxNotional</c>, the rolling-notional
/// ledger, and the margin reservation) under-counts option notional
/// by a factor of <c>contractMultiplier</c> — a 100x silent bypass of
/// every <c>MaxNotional</c> cap on PETR-class option series with the
/// upstream sample multiplier of 100.
/// </para>
///
/// <para>
/// Fail-open contract: unknown symbols fall back to the equity
/// formula (<c>price * quantity</c>, multiplier = 1). Same posture as
/// <see cref="ITickSizeProvider"/> — a symbol missing from the
/// directory must never make a notional gate fire spurious rejects.
/// The cost of fail-open is bounded because (a) all production
/// option symbols come from the same SymbolDirectory that carries the
/// multiplier and (b) Fase 2 (#454/#486) replaces the static
/// directory with the SDK-driven <c>SecurityDefinitionEvent</c>
/// projection, eliminating the configuration gap entirely.
/// </para>
/// </summary>
public interface IMarketValueCalculator
{
    /// <summary>
    /// Returns the gross notional for the given (symbol, price, qty).
    /// Callers pass the LIMIT price (or a reference price for market
    /// orders); the calculator does not know about order type, side,
    /// or fees — those concerns stay with the risk checks that
    /// consume the value.
    /// </summary>
    decimal GetNotional(string symbol, decimal price, long quantity);
}

/// <summary>
/// OPT-B (#484). Equity-only fallback that returns the historical
/// <c>price * quantity</c> with no multiplier lookup. Exposed as a
/// shared singleton so test fixtures that construct risk checks
/// directly (no DI) can pass it without recreating a directory; the
/// equity-only default is also wired into the consumer ctors as a
/// fallback for missing DI registrations.
/// </summary>
public sealed class EquityMarketValueCalculator : IMarketValueCalculator
{
    public static readonly EquityMarketValueCalculator Instance = new();

    public decimal GetNotional(string symbol, decimal price, long quantity)
        => price * quantity;
}
