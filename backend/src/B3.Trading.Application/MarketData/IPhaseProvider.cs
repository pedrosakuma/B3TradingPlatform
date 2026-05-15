namespace B3.Trading.Application.MarketData;

/// <summary>
/// Read-only seam exposing the current trading-phase of an instrument.
///
/// <para>
/// Owned by Q1.5 (#257) and consumed by the risk pipeline (#254) so the
/// GFA / auction-only checks can decide whether a given order is allowed
/// in the current session phase without leaking the auction state-store
/// internals into the order path.
/// </para>
///
/// <para>
/// The implementation lives in this assembly (<see cref="AuctionStateStore"/>)
/// and is fed off the UMDF market-data listener. Tests and #254 unit
/// suites can wire a fake that returns a fixed phase per symbol.
/// </para>
/// </summary>
public interface IPhaseProvider
{
    /// <summary>
    /// Returns the last-observed phase for <paramref name="symbol"/>.
    /// Returns <see cref="TradingPhase.Unknown"/> for symbols never
    /// touched by an auction frame — that lets risk reject (or fail
    /// closed) cleanly on unseen instruments.
    /// </summary>
    TradingPhase GetPhase(string symbol);
}

/// <summary>
/// Coarse trading-phase enumeration mirroring B3's session model.
///
/// <para>
/// Values are deliberately a superset of what the host actually
/// derives today (see <see cref="AuctionStateStore"/> docs for the
/// heuristic) so #254 risk checks can pattern-match against the full
/// vocabulary without an enum migration when upstream
/// (B3MatchingPlatform#321/#322) starts emitting explicit phase
/// transitions.
/// </para>
/// </summary>
public enum TradingPhase
{
    Unknown,
    Reserved,
    OpeningCall,
    Open,
    FinalClosingCall,
    Close,
}
