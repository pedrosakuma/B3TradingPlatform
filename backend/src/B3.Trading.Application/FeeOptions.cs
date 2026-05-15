namespace B3.Trading.Application;

/// <summary>
/// Q2.3 (#270). Configurable per-fill fee schedule consumed by
/// <see cref="BpsFeeCalculator"/>. Bound from the
/// <c>Trading:Fees</c> configuration section via the standard
/// <c>IOptionsMonitor</c> pattern, so changes through configuration
/// hot-reload propagate to the next computed fill without restart.
///
/// <para>
/// All <c>*Bps</c> fields are basis points of notional (1 bps =
/// <c>0.01%</c>). The B3 emolumentos and CBLC liquidação rates are
/// <b>placeholders</b> for the real schedule; they are deliberately set
/// to conservative cash-equity defaults so the fees pipeline produces
/// non-zero numbers in dogfood while leaving the real numbers to a
/// follow-up that pulls the published B3 fee table.
/// </para>
///
/// <para>
/// <see cref="Overrides"/> lets a single symbol diverge from the
/// defaults — e.g. equity options or futures with a different brokerage
/// schedule. Lookup is exact-match, case-sensitive (matches the symbol
/// casing the gateway uses on the wire); a missing override falls back
/// to the top-level defaults verbatim.
/// </para>
/// </summary>
public sealed class FeeOptions
{
    public const string SectionName = "Trading:Fees";

    /// <summary>
    /// Brokerage in basis points of notional (1 bps = 0.01%).
    /// Default of <c>5 bps</c> is a typical online-broker cash-equity
    /// rate; replace per deployment.
    /// </summary>
    public decimal BrokerageBps { get; set; } = 5m;

    /// <summary>
    /// Floor brokerage charged per execution regardless of notional.
    /// In BRL. Used by <see cref="BpsFeeCalculator"/> via
    /// <c>max(notional * bps / 10_000, min)</c>.
    /// </summary>
    public decimal BrokerageMin { get; set; } = 2m;

    /// <summary>
    /// Placeholder B3 emolumentos rate (cash equities, conservative
    /// 3.25 bps). Real schedule is segment-dependent and tiered; this
    /// will be replaced by a lookup against the published table in a
    /// follow-up issue.
    /// </summary>
    public decimal EmolumentosBps { get; set; } = 3.25m;

    /// <summary>
    /// Placeholder CBLC liquidação rate (conservative 2.75 bps).
    /// Same caveat as <see cref="EmolumentosBps"/>.
    /// </summary>
    public decimal LiquidacaoBps { get; set; } = 2.75m;

    /// <summary>
    /// Per-symbol overrides. List shape (rather than nested dict) keeps
    /// env-var binding ergonomic:
    /// <c>Trading__Fees__Overrides__0__Symbol=PETR4</c>. Exact-match
    /// lookup; symbols without an entry fall back to the top-level
    /// defaults.
    /// </summary>
    public List<FeeSymbolOverride> Overrides { get; set; } = new();
}

/// <summary>
/// Per-symbol override for <see cref="FeeOptions"/>. Every field is
/// independent — leave at its default to inherit from the top-level
/// defaults (the calculator merges field-by-field, not whole-record).
/// </summary>
public sealed class FeeSymbolOverride
{
    public string Symbol { get; set; } = string.Empty;
    public decimal? BrokerageBps { get; set; }
    public decimal? BrokerageMin { get; set; }
    public decimal? EmolumentosBps { get; set; }
    public decimal? LiquidacaoBps { get; set; }
}
