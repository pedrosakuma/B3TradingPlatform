namespace B3.Trading.Application.Risk;

/// <summary>
/// B3 trading-phase ladder for an instrument as observed by the
/// market-data side (auction lifecycle). Distinct from
/// <see cref="B3.Trading.Domain.SessionPhase"/> which is the broader
/// venue-level surface that <see cref="Checks.SessionPhaseCheck"/>
/// gates on. The two will eventually consolidate (#257 wires the real
/// <see cref="IPhaseProvider"/> from the auction-MD ingest); for now
/// they coexist because Q1.2 (#254) needs the finer-grained call /
/// closing-call distinction to gate <c>GoodForAuction</c> while the
/// session-phase axis still describes "is the venue continuous,
/// after-hours, closed".
/// </summary>
public enum TradingPhase
{
    /// <summary>Pre-trade reservation window — orders accepted but no matching.</summary>
    Reserved,
    /// <summary>Opening call auction — accepts <c>GoodForAuction</c>.</summary>
    OpeningCall,
    /// <summary>Continuous matching.</summary>
    Open,
    /// <summary>Closing call auction (a.k.a. "leilão de fechamento") — accepts <c>GoodForAuction</c>.</summary>
    FinalClosingCall,
    /// <summary>After-hours / venue closed for matching.</summary>
    Close,
    /// <summary>Phase information not available for the symbol (e.g. no MD subscription).</summary>
    Unknown,
}

/// <summary>
/// Per-symbol view of the current B3 <see cref="TradingPhase"/>.
/// Backs <see cref="Checks.GoodForAuctionPhaseCheck"/> (#254) and will
/// be the seam through which #257 publishes auction-MD-driven phase
/// transitions.
///
/// <para><b>Why a new interface (and not an extension of
/// <see cref="SessionPhaseService"/>):</b> session phase is a venue-
/// wide control surface owned by the operator (kill-switch-adjacent);
/// trading phase is a market-data fact reported by the venue per
/// instrument. They have different write paths, different durabilities,
/// and overlap only loosely on the "is matching active" question.
/// Forcing one to model the other makes both worse.</para>
/// </summary>
public interface IPhaseProvider
{
    /// <summary>
    /// Returns the current <see cref="TradingPhase"/> for the symbol,
    /// or <see cref="TradingPhase.Unknown"/> when no phase information
    /// is available. Implementations must be cheap and side-effect-free
    /// — the pipeline calls this on the hot path.
    /// </summary>
    TradingPhase GetPhase(string symbol);
}

/// <summary>
/// <b>TEMPORARY default until #257 wires the auction-MD-driven provider.</b>
/// Always reports <see cref="TradingPhase.Open"/> so the rest of the
/// risk pipeline can run without forcing #257 to land first. The
/// consequence is that <see cref="Checks.GoodForAuctionPhaseCheck"/>
/// will <i>always reject</i> a <c>GoodForAuction</c> TIF under this
/// stub (Open ∉ {OpeningCall, FinalClosingCall}) — which is the
/// intended fail-closed posture: GFA is a low-volume specialty TIF
/// and a silent accept here would route those orders into continuous
/// matching, where <c>GoodForAuction</c> is meaningless.
///
/// <para>When the real provider is registered (#257) it must replace
/// this singleton in DI, not coexist — the pipeline resolves a single
/// <see cref="IPhaseProvider"/>.</para>
/// </summary>
public sealed class NoPhaseProvider : IPhaseProvider
{
    public TradingPhase GetPhase(string symbol) => TradingPhase.Open;
}
