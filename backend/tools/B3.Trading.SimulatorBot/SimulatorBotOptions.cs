using System.ComponentModel.DataAnnotations;

namespace B3.Trading.SimulatorBot;

/// <summary>
/// Bound from the <c>Bot:</c> section of configuration. The bot opens a
/// single FIXP session against matching-platform and submits a steady
/// flow of orders against a pre-configured instrument list. See
/// <c>docker/docker-compose.simulator-bot.yml</c> for the canonical
/// docker-side wiring.
/// </summary>
public sealed class SimulatorBotOptions
{
    public const string SectionName = "Bot";

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

    public string SenderLocation { get; set; } = "BOT";
    public string EnteringTrader { get; set; } = "BOT";

    /// <summary>Local directory for the SDK's session state store. Mount to
    /// a docker volume so SessionVerId survives container restarts.</summary>
    public string StateDirectory { get; set; } = "/var/lib/b3-simulator-bot";

    /// <summary>How often the submit loop wakes up. Each tick may submit
    /// zero or more orders depending on <see cref="MaxInFlightPerSymbol"/>.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Cap on working orders per symbol. The bot stops submitting
    /// for that symbol when the cap is hit; cancels free up slots.</summary>
    public int MaxInFlightPerSymbol { get; set; } = 5;

    /// <summary>If &gt; 0, the bot cancels still-working orders older than
    /// this. Keeps the book from accumulating stale resting volume.</summary>
    public TimeSpan AutoCancelAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Probability a tick produces a crossing (taker) order
    /// instead of resting liquidity. 0..1.</summary>
    public double CrossProbability { get; set; } = 0.25;

    /// <summary>Optional deterministic seed for the Random used by the
    /// pattern generator. <c>null</c> seeds from the system clock.</summary>
    public int? RandomSeed { get; set; }

    [Required, MinLength(1)]
    public List<InstrumentConfig> Instruments { get; set; } = new();
}

/// <summary>One instrument the bot trades. <see cref="SecurityId"/> must
/// match matching's <c>instruments-eqt.json</c>; <see cref="RefPrice"/>
/// anchors the random-walk pricing.</summary>
public sealed class InstrumentConfig
{
    [Required] public string Symbol { get; set; } = string.Empty;
    [Required] public ulong SecurityId { get; set; }
    [Required, Range(typeof(decimal), "0.01", "1000000")]
    public decimal RefPrice { get; set; }

    /// <summary>Minimum price increment. Prices are rounded to this.</summary>
    public decimal TickSize { get; set; } = 0.01m;

    /// <summary>Round-lot size; quantities are multiples of this.</summary>
    public long LotSize { get; set; } = 100;

    /// <summary>Min lots per order (inclusive).</summary>
    public int MinLots { get; set; } = 1;

    /// <summary>Max lots per order (inclusive).</summary>
    public int MaxLots { get; set; } = 5;
}
