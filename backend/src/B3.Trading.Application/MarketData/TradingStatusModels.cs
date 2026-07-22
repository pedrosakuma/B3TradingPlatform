namespace B3.Trading.Application.MarketData;

/// <summary>
/// Per-symbol trading status delta surfaced by <see cref="IMarketDataSubscriber"/>
/// (Stage A of #370). Derived from <c>InfoSnapshot.TradingStatus</c>
/// transitions: the SDK delivers info snapshots as cumulative state,
/// the adapter remembers the last status per symbol and raises this
/// event only when the value actually changes.
///
/// <para>
/// <see cref="NewStatus"/> is the raw SBE
/// <c>SecurityTradingStatus</c> code from the upstream
/// <c>B3.Umdf.Mbo.Sbe.V16</c> schema; consumers map it via
/// <see cref="SecurityTradingStatusCodes"/>. <see cref="PreviousStatus"/>
/// is null on the first observation for a symbol.
/// </para>
/// </summary>
public readonly record struct MarketTradingStatusChange(
    string Symbol,
    ulong SecurityId,
    long? PreviousStatus,
    long NewStatus,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// Raw SBE <c>SecurityTradingStatus</c> values from the upstream
/// market-data schema (B3 UMDF v2.2). Centralised here so the venue
/// halt subscriber and any future consumer share a single source of
/// truth and don't sprinkle magic numbers across the codebase.
///
/// <para>
/// We surface only the codes the application actually interprets.
/// Anything not listed (e.g. <c>CLOSE = 4</c>, <c>RESERVED = 21</c>,
/// <c>FINAL_CLOSING_CALL = 101</c>) is a scheduled session-phase
/// transition handled by <c>SessionPhaseService</c>, not a halt.
/// </para>
/// </summary>
public static class SecurityTradingStatusCodes
{
    /// <summary>Trading halt (manual / regulatory pause).</summary>
    public const long Pause = 2;

    /// <summary>Instrument is trading normally.</summary>
    public const long Open = 17;

    /// <summary>Not available for trading (forbidden).</summary>
    public const long Forbidden = 18;

    /// <summary>
    /// True iff the status represents a halt-equivalent stop on order
    /// submission (PAUSE or FORBIDDEN). The other documented values
    /// — CLOSE, RESERVED, FINAL_CLOSING_CALL — are scheduled phases
    /// and are deliberately NOT treated as halts here.
    /// </summary>
    public static bool IsHalt(long status) =>
        status == Pause || status == Forbidden;

    /// <summary>True iff the status represents continuous trading
    /// (i.e. the venue is open). Used to clear a venue-origin halt
    /// when the upstream resumes.</summary>
    public static bool IsOpen(long status) => status == Open;
}

/// <summary>
/// Origin of a <c>SymbolHaltService</c> halt. Operator and venue
/// halts coexist as independent flags on the same symbol — a symbol
/// is halted iff at least one origin has it halted. This means:
/// <list type="bullet">
///   <item>An operator halt is NEVER cleared by a venue resume
///         (operator stays in control).</item>
///   <item>A venue halt is NEVER cleared by an operator resume
///         (the venue is still rejecting orders; clearing would
///         create a false sense of safety).</item>
/// </list>
/// </summary>
public enum HaltOrigin
{
    /// <summary>Local operator halt via <c>/api/admin/halts</c>.</summary>
    Operator = 0,

    /// <summary>Venue-originated halt observed via market data
    /// (Stage A: <c>SecurityTradingStatus</c> deltas; Stage B: a
    /// dedicated SDK event once available).</summary>
    Venue = 1,
}
