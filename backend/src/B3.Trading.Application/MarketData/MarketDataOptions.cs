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
}
