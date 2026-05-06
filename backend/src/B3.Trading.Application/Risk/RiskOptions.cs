namespace B3.Trading.Application.Risk;

/// <summary>
/// Risk configuration. Bound from <c>Trading:Risk</c>. Resolution order
/// when computing limits for an order: per-end-client → per-firm →
/// per-symbol → default. First non-null wins per field.
/// </summary>
public sealed class RiskOptions
{
    public const string SectionName = "Trading:Risk";

    public RiskLimits Default { get; set; } = new();
    public Dictionary<string, RiskLimits> PerEndClient { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RiskLimits> PerFirm { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RiskLimits> PerSymbol { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> ReferencePrices { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public MarginOptions Margin { get; set; } = new();

    /// <summary>
    /// Rolling-window notional cap (slice 7). Modeled as its own
    /// section instead of a field on <see cref="RiskLimits"/> because
    /// the underlying ledger is keyed per-end-client (and per-firm)
    /// only — letting the per-symbol resolution slot participate
    /// would mean the cap that applies to one order varies with the
    /// symbol while the state being measured is global, which is the
    /// inconsistency we want to avoid.
    /// </summary>
    public RollingNotionalOptions RollingNotional { get; set; } = new();

    /// <summary>
    /// Order rate limit (slice 7). Same scoping rationale as
    /// <see cref="RollingNotional"/>: per-end-client and per-firm
    /// only, no per-symbol override.
    /// </summary>
    public OrderRateOptions OrderRate { get; set; } = new();
}

/// <summary>
/// Per-end-client / per-firm cap on the notional submitted within a
/// rolling time window. Anti-runaway guard, not a regulatory boundary
/// — the check/record cycle is not atomic, so under bursts the cap
/// can be overshot by the number of concurrent in-flight submits.
/// </summary>
public sealed class RollingNotionalOptions
{
    public int WindowSeconds { get; set; } = 60;
    public RollingNotionalLimit Default { get; set; } = new();
    public Dictionary<string, RollingNotionalLimit> PerEndClient { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RollingNotionalLimit> PerFirm { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RollingNotionalLimit
{
    public decimal? Cap { get; set; }
}

/// <summary>
/// Per-end-client / per-firm cap on the number of submitted orders
/// within a rolling time window. Both ledgers (per-end-client and
/// per-firm) are checked independently when configured; the order is
/// rejected on the first cap exceeded.
/// </summary>
public sealed class OrderRateOptions
{
    public int WindowSeconds { get; set; } = 1;
    public OrderRateLimit Default { get; set; } = new();
    public Dictionary<string, OrderRateLimit> PerEndClient { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OrderRateLimit> PerFirm { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OrderRateLimit
{
    public int? Max { get; set; }
}

/// <summary>
/// Reserve-on-submit margin configuration. Disabled by default —
/// <see cref="NoOpMarginProvider"/> stays in place until an operator
/// opts in. See <c>docs/rfcs/pre-trade-risk-v2.md</c> §3.1 for the
/// model assumed by v2 (crypto-spot ledger; not T+N, not derivatives).
/// </summary>
public sealed class MarginOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-end-client opening balance (in the single accounting
    /// currency v2 assumes). Missing entries are treated as zero, so
    /// unrecognized end-clients cannot place buy orders when margin
    /// is enabled — fail-closed by default.
    /// </summary>
    public Dictionary<string, decimal> Initial { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RiskLimits
{
    public long? MaxQuantity { get; set; }
    public decimal? MaxNotional { get; set; }

    /// <summary>
    /// Optional notional floor (price × quantity) for Limit orders.
    /// Rejects sub-floor submissions as anti-dust / typo-guard
    /// (e.g. fat-finger 0.01 on PETR4 still passes max-quantity but
    /// trips min-notional). Market orders skip the check — there's
    /// no price to evaluate and the venue + max-quantity gates
    /// already bound the worst case. Default semantics when unset
    /// everywhere in the precedence chain are permissive: no floor
    /// (see <see cref="Risk.Checks.MinNotionalCheck"/>).
    /// </summary>
    public decimal? MinNotional { get; set; }
    public decimal? PriceCollarPercent { get; set; }
    /// <summary>
    /// Optional absolute price band (in price units) around the
    /// reference. When set together with <see cref="PriceCollarPercent"/>
    /// the effective band is the **intersection** of the two —
    /// whichever is narrower wins on each side. Useful for low-priced
    /// or illiquid tickers where a percent-only collar is too coarse,
    /// or as a conservative floor on top of an existing percent.
    /// </summary>
    public decimal? PriceCollarAbsolute { get; set; }
    public long? PositionLimit { get; set; }

    /// <summary>
    /// Optional cap on the number of non-terminal orders an end-client
    /// can have outstanding at once. Counted across all symbols by the
    /// owner index — the per-symbol resolver slot is intentionally
    /// orthogonal (a per-symbol cap would mean "no more than N
    /// PETR4 orders open" but the underlying index is per-owner; we
    /// resolve the field per-symbol but the count is global, same
    /// trade-off as <see cref="PositionLimit"/>).
    /// </summary>
    public int? MaxOpenOrders { get; set; }

    /// <summary>
    /// Whether the seller is allowed to take a Sell that would
    /// drive their projected net position negative. B3 cash equities
    /// (mercado à vista) does not allow naked shorts — a Sell must
    /// be covered by long inventory (or, in a future iteration, by
    /// borrowed stock from a BTC registry). Default semantics when
    /// unset everywhere in the precedence chain are conservative:
    /// naked short is **blocked** (see
    /// <see cref="Risk.Checks.NoNakedShortCheck"/>). Set to <c>true</c>
    /// per-firm or per-end-client to opt out (e.g. authorised
    /// market makers, or test accounts).
    /// </summary>
    public bool? AllowShortSell { get; set; }

    /// <summary>
    /// Whether the end-client may self-cross — i.e. submit an order
    /// that would match against one of their own opposite-side working
    /// orders on the same symbol. Default semantics when unset
    /// everywhere in the precedence chain are conservative: self-trade
    /// is **blocked** with newest-rejects (see
    /// <see cref="Risk.Checks.SelfTradePreventionCheck"/>). Set to
    /// <c>true</c> per-firm or per-end-client to opt out (e.g.
    /// market-maker accounts or test scenarios).
    /// </summary>
    public bool? AllowSelfTrade { get; set; }
}

public static class RiskLimitsResolver
{
    /// <summary>
    /// Resolves a single risk-limit field by walking the precedence
    /// chain <c>per-end-client → per-firm → per-symbol → default</c>
    /// and returning the first non-null value. <paramref name="firmId"/>
    /// may be null/blank when the caller has no firm context (legacy
    /// callers); the per-firm slot is then skipped.
    /// </summary>
    public static T? Resolve<T>(
        RiskOptions opts,
        string endClient,
        string? firmId,
        string symbol,
        Func<RiskLimits, T?> selector)
        where T : struct
    {
        if (opts.PerEndClient.TryGetValue(endClient, out var ec) && selector(ec).HasValue)
            return selector(ec);
        if (!string.IsNullOrWhiteSpace(firmId)
            && opts.PerFirm.TryGetValue(firmId, out var fi)
            && selector(fi).HasValue)
            return selector(fi);
        if (opts.PerSymbol.TryGetValue(symbol, out var sy) && selector(sy).HasValue)
            return selector(sy);
        return selector(opts.Default);
    }

    /// <summary>
    /// Convenience: resolve every <see cref="RiskLimits"/> field in
    /// one pass. Used by <c>GET /admin/risk/limits</c> to surface
    /// what the system actually thinks the cap is for a given
    /// (endClient, firm, symbol) tuple.
    /// </summary>
    public static RiskLimits ResolveAll(
        RiskOptions opts, string endClient, string? firmId, string symbol) =>
        new()
        {
            MaxQuantity = Resolve(opts, endClient, firmId, symbol, l => l.MaxQuantity),
            MaxNotional = Resolve(opts, endClient, firmId, symbol, l => l.MaxNotional),
            MinNotional = Resolve(opts, endClient, firmId, symbol, l => l.MinNotional),
            PriceCollarPercent = Resolve(opts, endClient, firmId, symbol, l => l.PriceCollarPercent),
            PriceCollarAbsolute = Resolve(opts, endClient, firmId, symbol, l => l.PriceCollarAbsolute),
            PositionLimit = Resolve(opts, endClient, firmId, symbol, l => l.PositionLimit),
            MaxOpenOrders = Resolve(opts, endClient, firmId, symbol, l => l.MaxOpenOrders),
            AllowShortSell = Resolve(opts, endClient, firmId, symbol, l => l.AllowShortSell),
            AllowSelfTrade = Resolve(opts, endClient, firmId, symbol, l => l.AllowSelfTrade),
        };
}
