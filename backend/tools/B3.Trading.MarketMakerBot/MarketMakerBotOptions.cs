using System.ComponentModel.DataAnnotations;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Bound from the <c>MarketMaker:</c> section of configuration. The bot
/// opens a single FIXP session against matching-platform and behaves as
/// a co-located two-sided market maker: one resting bid + one resting
/// ask per configured instrument at all times, re-quoted immediately
/// whenever a side is filled, cancelled, or rejected. See
/// <c>docker/docker-compose.market-maker.yml</c> for the canonical
/// docker-side wiring.
/// </summary>
public sealed class MarketMakerBotOptions
{
    public const string SectionName = "MarketMaker";

    /// <summary>FIXP TCP listener exposed by matching-platform (host:port).</summary>
    [Required] public string Endpoint { get; set; } = "matching-platform:9876";

    /// <summary>Matching FIXP session id (must exist in matching's <c>sessions[]</c>).</summary>
    [Required] public uint SessionId { get; set; }

    /// <summary>EnteringFirm code from matching's <c>firms[].enteringFirmCode</c>.</summary>
    [Required] public uint EnteringFirm { get; set; }

    /// <summary>Configured floor for the FIXP <c>SessionVerId</c>; the SDK's
    /// <c>FileSessionStateStore</c> bumps from this on warm restart.</summary>
    public uint SessionVerId { get; set; } = 1;

    /// <summary>Verbatim JSON credential payload — matching's FixpSession
    /// expects <c>{"auth_type","username","access_key"}</c>.</summary>
    [Required] public string AccessKey { get; set; } = string.Empty;

    public string SenderLocation { get; set; } = "MM";
    public string EnteringTrader { get; set; } = "MM";

    /// <summary>Local directory for the SDK's session state store. Mount to
    /// a docker volume so SessionVerId survives container restarts.</summary>
    public string StateDirectory { get; set; } = "/var/lib/b3-market-maker-bot";

    /// <summary>
    /// Defensive periodic reconciliation interval — checks that every
    /// configured (instrument, side) still has a resting quote and
    /// re-submits if not. This is NOT the primary re-quote path (that's
    /// event-driven, see <c>MarketMakerWorker.HandleEventAsync</c>); it's
    /// a safety net against missed events or gaps after a reconnect.
    /// </summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Required, MinLength(1)]
    public List<InstrumentConfig> Instruments { get; set; } = new();

    /// <summary>Live market-data anchor. Optional by design: if unset the
    /// bot degrades gracefully to quoting off each instrument's static
    /// <see cref="InstrumentConfig.RefPrice"/> only (same fallback shape
    /// as the trading-host's own market-data gate) rather than failing to
    /// start — a co-located bot without a feed is still useful liquidity
    /// in a pinch.</summary>
    public MarketDataOptions MarketData { get; set; } = new();
}

/// <summary>Connection to B3MarketDataPlatform's WebSocket feed (see
/// <c>B3.MarketData.WebSocketClient</c>). Anchors quote pricing on the
/// live market instead of a static config value and pauses quoting on
/// <c>SymbolDelisted</c> — see <see cref="MarketDataFeed"/>.</summary>
public sealed class MarketDataOptions
{
    /// <summary>WebSocket endpoint, e.g. <c>ws://market-data-platform:8080/ws</c>.
    /// Leave unset to run with static RefPrice anchors only.</summary>
    public string? WsUrl { get; set; }
}

/// <summary>One instrument the bot quotes. <see cref="SecurityId"/> must
/// match matching's <c>instruments-eqt.json</c>; <see cref="RefPrice"/>
/// anchors the deterministic quote pricing.</summary>
public sealed class InstrumentConfig
{
    [Required] public string Symbol { get; set; } = string.Empty;
    [Required] public ulong SecurityId { get; set; }
    [Required, Range(typeof(decimal), "0.01", "1000000")]
    public decimal RefPrice { get; set; }

    /// <summary>Minimum price increment. Quotes are rounded to this.</summary>
    [Range(typeof(decimal), "0.0001", "100000")]
    public decimal TickSize { get; set; } = 0.01m;

    /// <summary>Round-lot size; quote quantity is a multiple of this.</summary>
    [Range(1, long.MaxValue)]
    public long LotSize { get; set; } = 100;

    /// <summary>Quote size for each side, in lots.</summary>
    [Range(1, int.MaxValue)]
    public int QuoteLots { get; set; } = 1;

    /// <summary>Distance in ticks from <see cref="RefPrice"/> for each
    /// side's quote (symmetric spread). E.g. <c>SpreadTicks=5</c>,
    /// <c>TickSize=0.01</c> → bid = RefPrice - 0.05, ask = RefPrice + 0.05.
    /// Must be small enough that the bid stays positive; see
    /// <see cref="RefPrice"/>.</summary>
    [Range(0, int.MaxValue)]
    public int SpreadTicks { get; set; } = 5;
}
