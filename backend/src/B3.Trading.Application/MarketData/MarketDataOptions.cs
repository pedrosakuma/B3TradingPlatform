namespace B3.Trading.Application.MarketData;

/// <summary>
/// Configuration for the live market-data reference-price source.
/// Bound from <c>Trading:MarketData</c>.
///
/// <para>
/// When <see cref="WsUrl"/> is null/whitespace, the host registers
/// only <see cref="Risk.ConfigReferencePrice"/> and the price collar
/// remains driven by the static <c>Trading:Risk:ReferencePrices</c>
/// dictionary. When <see cref="WsUrl"/> is set, a <see cref="MarketDataReferencePrice"/>
/// is wired in front of it, transparently feeding the cache from
/// trade/info events and falling back to the static dictionary on
/// cache miss or staleness.
/// </para>
/// </summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "Trading:MarketData";

    /// <summary>
    /// WebSocket endpoint of B3MarketDataPlatform (e.g.
    /// <c>ws://marketdata:8080/ws</c>). Null/empty disables the live
    /// source — see class docs.
    /// </summary>
    public string? WsUrl { get; set; }

    /// <summary>
    /// Symbols to subscribe to on startup. Configure via array binding
    /// (<c>Trading__MarketData__Symbols__0=PETR4</c>) or a list in
    /// appsettings. Empty + <see cref="WsUrl"/> set is a misconfig
    /// (logged as a warning at startup).
    /// </summary>
    public string[] Symbols { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Maximum age of a cached price before <see cref="MarketDataReferencePrice"/>
    /// stops trusting it and falls through to the static reference-price
    /// fallback. Default 5 minutes — large enough for an idle book to
    /// keep enforcing the collar across a quick lull, small enough that
    /// a dead feed doesn't keep approving against last week's price.
    /// Set <see cref="TimeSpan.Zero"/> or negative to disable the check
    /// (cache wins forever as long as it has a value).
    /// </summary>
    public TimeSpan MaxStaleness { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Q3.6 Stage A (#286). Opt-in flag to subscribe to the L3 / MBO
    /// book stream (<c>SubscribeFlags.Book</c>) in addition to
    /// <c>Trades | Info</c>. When <c>true</c>, every symbol passed to
    /// <see cref="IMarketDataSubscriber.SubscribeAsync"/> receives the
    /// per-order add/update/delete + book-snapshot stream, which the
    /// host-side <see cref="InMemoryL2BookView"/> / SDK adapter
    /// assembles into a derived L2 top-of-book consumed by
    /// <c>MboPegBookPump</c> for pegged-algo recalc.
    /// <para>
    /// Default <c>false</c> so existing deployments (which only need
    /// trades + info for the collar / VWAP estimator) pay nothing for
    /// the MBO bandwidth + per-order CPU cost. Set to <c>true</c> only
    /// when pegged algos are enabled.
    /// </para>
    /// </summary>
    public bool EnableBook { get; set; } = false;

    /// <summary>
    /// OPT-D (#486, refs #454 Fase 2). Opt-in flag to subscribe to the
    /// SDK <c>SecurityDefinition</c> channel
    /// (<c>SubscribeFlags.SecurityDefinition</c>, 0x20) introduced in
    /// upstream <c>pedrosakuma/B3MarketDataPlatform#55</c> / SDK 0.5.0.
    /// When <c>true</c>, every symbol passed to
    /// <see cref="IMarketDataSubscriber.SubscribeAsync"/> receives a
    /// bootstrap + delta stream of <c>SecurityDefinitionEvent</c> frames
    /// carrying tick (<c>MinPriceIncrement</c>), lot
    /// (<c>MinTradeVolume</c>), <c>ContractMultiplier</c>, option
    /// metadata (<c>StrikePrice</c>, <c>MaturityDate</c>,
    /// <c>PutOrCall</c>, <c>ExerciseStyle</c>), <c>SecurityType</c> and
    /// the rest of the static instrument metadata. The host adapter
    /// projects those frames into <see cref="SecurityDefinitionRegistry"/>,
    /// which the tick / lot / market-value providers consult before
    /// falling back to the operator-configured
    /// <see cref="SymbolDirectory"/>.
    /// <para>
    /// Default <c>true</c>: once the SDK is bumped past 0.5.0 we always
    /// want venue-pushed metadata to win over hand-typed config because
    /// the OPT umbrella ships hundreds of option series per underlying
    /// and config-only entry is infeasible. Set to <c>false</c> as an
    /// emergency kill-switch only — providers will then keep returning
    /// the static <see cref="SymbolDirectory"/> values exclusively
    /// (legacy behaviour).
    /// </para>
    /// </summary>
    public bool EnableSecurityDefinition { get; set; } = true;

    /// <summary>
    /// OPT-E (#487). Opt-in flag to subscribe to the SDK
    /// <c>PriceBand</c> channel (<c>SubscribeFlags.PriceBand</c>,
    /// 0x40) introduced in upstream
    /// <c>pedrosakuma/B3MarketDataPlatform#56</c> / SDK 0.6.0.
    /// When <c>true</c>, every symbol passed to
    /// <see cref="IMarketDataSubscriber.SubscribeAsync"/> receives a
    /// bootstrap + delta stream of <c>PriceBandEvent</c> frames
    /// carrying the venue's authoritative dynamic price band
    /// (<c>LowerBand</c> / <c>UpperBand</c>, plus
    /// <c>TradingReferencePrice</c>, <c>PriceLimitType</c>,
    /// <c>PriceBandType</c> discriminators). The host adapter
    /// projects those frames into <see cref="PriceBandRegistry"/>,
    /// which the new pre-trade
    /// <see cref="Risk.Checks.PriceBandCheck"/> consults as the source
    /// of truth — replacing the static-config fat-finger collar
    /// (<see cref="Risk.Checks.PriceCollarCheck"/>) with the venue
    /// truth on a per-symbol intraday basis.
    /// <para>
    /// Default <c>true</c>: once the SDK is bumped past 0.6.0 we
    /// always want venue-pushed bands as authoritative, same
    /// rationale as <see cref="EnableSecurityDefinition"/>. Set to
    /// <c>false</c> as an emergency kill-switch only — the price-band
    /// check then becomes a no-op (fail-open) and the collar takes
    /// over exclusively.
    /// </para>
    /// </summary>
    public bool EnablePriceBand { get; set; } = true;
}
