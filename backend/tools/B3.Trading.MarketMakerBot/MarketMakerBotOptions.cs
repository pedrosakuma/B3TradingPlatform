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

    /// <summary>
    /// RFC #703 miss-fill / staleness guard: the SDK exposes no mass
    /// order-status query, so there is no way to ask the venue "what do I
    /// actually have open right now" — this is a purely time-based
    /// heuristic instead. Any resting order older than this is cancelled
    /// (and, via the normal <c>OrderCancelled</c> event path, immediately
    /// re-quoted) by <see cref="ReconcileLoopAsync"/> on every tick, so a
    /// resting order the bot's own event stream silently dropped (a
    /// "miss-fill") can't linger on the book indefinitely.
    /// </summary>
    public TimeSpan MaxOrderAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// RFC #703 client-side safety cap, defense in depth against the exact
    /// failure mode that produced <c>pedrosakuma/B3MatchingPlatform#567</c>
    /// (73k+ resting orders overflowing matching's fixed snapshot buffer):
    /// once the bot's own tracked open-order count reaches this, it stops
    /// submitting NEW quotes (existing resting orders are left alone —
    /// this is not a panic-cancel) and logs loudly. There is normally at
    /// most one resting order per (instrument, side), so this should only
    /// ever trip if something upstream (venue or bot bug) is preventing
    /// orders from actually terminating.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxOpenOrders { get; set; } = 500;

    /// <summary>
    /// RFC #703 book-driven quoting: minimum distance (in <see
    /// cref="InstrumentConfig.TickSize"/> multiples) a side's resting
    /// price may drift from the freshly-computed target price before a
    /// market-data book change (see <see cref="MarketDataFeed.BookOrderChanged"/>)
    /// triggers a reactive cancel-and-requote. Kept nonzero so a
    /// no-op-sized book wiggle doesn't churn a perfectly fine quote.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RequoteDeviationTicks { get; set; } = 2;

    /// <summary>
    /// RFC #703 book-driven quoting: minimum time a side must have been
    /// resting before a book-driven deviation can trigger a reactive
    /// cancel-and-requote for it. Throttles the reactive path so a burst
    /// of book updates for the same symbol can't repeatedly cancel a
    /// quote that itself hasn't even been acknowledged yet — the exact
    /// venue-flooding shape RFC #703 exists to prevent, just triggered
    /// from the market-data side this time instead of the ER side.
    /// </summary>
    public TimeSpan MinRequoteInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    [Required, MinLength(1)]
    public List<InstrumentConfig> Instruments { get; set; } = new();

    /// <summary>Live market-data anchor. Optional by design: if unset the
    /// bot degrades gracefully to quoting off each instrument's static
    /// <see cref="InstrumentConfig.RefPrice"/> only (same fallback shape
    /// as the trading-host's own market-data gate) rather than failing to
    /// start — a co-located bot without a feed is still useful liquidity
    /// in a pinch.</summary>
    public MarketDataOptions MarketData { get; set; } = new();

    /// <summary>Process-local P&amp;L snapshot and mark-freshness settings.</summary>
    public MarketMakerTelemetryOptions Telemetry { get; set; } = new();
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

public sealed class MarketMakerTelemetryOptions
{
    /// <summary>Cadence for structured per-symbol position/P&amp;L logs.</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum live-market mark age allowed for unrealized P&amp;L.
    /// Missing, disconnected, or older marks suppress that measurement.</summary>
    public TimeSpan MarkMaxAge { get; set; } = TimeSpan.FromSeconds(30);
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

    /// <summary>
    /// Optional inventory-aware mid-price shift. Disabled by default so the
    /// historical symmetric quote behavior remains unchanged.
    /// </summary>
    public InventorySkewConfig InventorySkew { get; set; } = new();

    /// <summary>
    /// Optional trade-driven widening added to <see cref="SpreadTicks"/>.
    /// Disabled by default so historical static-spread pricing is unchanged.
    /// </summary>
    public VolatilitySpreadConfig VolatilitySpread { get; set; } = new();
}

public sealed class InventorySkewConfig
{
    /// <summary>Whether this instrument's quotes react to process-local inventory.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute inventory, in lots, at which the configured maximum shift is
    /// reached. This is only a normalization/saturation band, not a risk limit.
    /// </summary>
    public long FullSkewAtLots { get; set; } = 10;

    /// <summary>Maximum absolute mid-price shift, in ticks.</summary>
    public decimal MaxSkewTicks { get; set; } = 5m;
}

public sealed class VolatilitySpreadConfig
{
    /// <summary>Whether valid market-data trades may widen this instrument's spread.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum age of absolute trade-to-trade move samples.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Hard per-symbol sample-count bound.</summary>
    public int MaxSamples { get; set; } = 120;

    /// <summary>Samples required before dynamic widening becomes active.</summary>
    public int MinSamples { get; set; } = 10;

    /// <summary>Scale applied to the arithmetic mean move in ticks before ceiling.</summary>
    public decimal Multiplier { get; set; } = 1m;

    /// <summary>Maximum dynamic ticks added to the configured half-spread.</summary>
    public int MaxAdditionalSpreadTicks { get; set; } = 20;
}
