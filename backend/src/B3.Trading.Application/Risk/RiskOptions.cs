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
    /// Q1.2 (#254). Maximum allowed horizon between "now" and a
    /// <see cref="B3.Trading.Domain.TimeInForce.GTD"/> order's
    /// <c>GoodTillDate</c>. Defaults to 30 days — picked to match
    /// the regulatory / clearing window most B3 brokers honor for
    /// good-till-date instructions. Bound at the global level only
    /// (not per-firm / per-symbol): a tenant-scoped override would
    /// just be a way to push expiries further out, which is the
    /// thing the cap exists to prevent.
    /// </summary>
    public TimeSpan MaxGtdHorizon { get; set; } = TimeSpan.FromDays(30);

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
/// Reserve-on-submit margin configuration. <b>Enabled by default</b>
/// (#416): the trading-platform factory default must be the safe
/// posture — an operator who forgets to flip this on otherwise gets
/// silently overspending accounts (cash ledger non-blocking by design;
/// the pre-trade guard lives here). Equivalent rationale to shipping
/// a database with foreign keys ON by default. See
/// <c>docs/rfcs/pre-trade-risk-v2.md</c> §3.1 for the model assumed
/// by v2 (crypto-spot ledger; not T+N, not derivatives).
///
/// <para>
/// Opt-out is explicit: set <c>Trading:Risk:Margin:Enabled=false</c>
/// to fall back to <see cref="NoOpMarginProvider"/> (e.g. test
/// compositions that do not seed cash). The host emits a startup
/// warning when this is disabled outside <c>Development</c> so the
/// drift is visible on dashboards.
/// </para>
///
/// <para>
/// <b>Migration note (#107 slice 4):</b> the per-end-client opening
/// balance has moved from <see cref="Initial"/> to the
/// <see cref="B3.Trading.Application.CashSeedOptions"/> ledger seeds
/// (<c>Trading:Cash:Seeds[]</c>) and to
/// <c>Trading:Cash:SignupInitialBalance</c> for self-service signup.
/// Operators populating <see cref="Initial"/> will get a one-time
/// startup warning with migration guidance; the fallback path stays
/// in place until a follow-up retires the property entirely.
/// </para>
/// </summary>
public sealed class MarginOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Pass-4 review (#299) P1. Bounded TTL for ambiguous-send
    /// replace reservations held under a
    /// <see cref="B3.Trading.Application.PendingReplacementRegistry"/>
    /// entry whose gateway dispatch threw post-Prepare. The intent
    /// is intentionally kept so a late Replaced ER can converge
    /// through <see cref="IReplaceMarginCoordinator.CommitReplace"/>
    /// without re-checking capacity (the upsize delta is already
    /// reserved). If no terminal ER arrives within this window, the
    /// reservation must be released or it leaks until the parent
    /// order terminates — and any concurrent order can NOT consume
    /// the held headroom in the meantime, so the cap is preserved
    /// strictly but the trader temporarily loses access to the
    /// upsize delta. 30s matches the typical venue ER round-trip
    /// upper bound on B3 (single-digit seconds is normal; a 30s
    /// silence indicates a real loss, not a slow ack). Tuneable via
    /// <c>Trading:Risk:Margin:AmbiguousReplaceTtl</c>.
    /// </summary>
    public TimeSpan AmbiguousReplaceTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-end-client opening balance (in the single accounting
    /// currency v2 assumes). Missing entries are treated as zero, so
    /// unrecognized end-clients cannot place buy orders when margin
    /// is enabled — fail-closed by default.
    ///
    /// <para>
    /// <b>Deprecated (#107 slice 4):</b> use
    /// <c>Trading:Cash:Seeds[]</c> for static opening balances and
    /// <c>Trading:Cash:SignupInitialBalance</c> for the self-service
    /// signup default. The <see cref="B3.Trading.Application.CashLedger"/>
    /// overlay introduced in slice 1 is the source of truth; this
    /// property is consulted only as a transition fallback for owners
    /// that have no ledger entry, and a follow-up will remove it
    /// entirely once dogfood configs migrate.
    /// </para>
    /// </summary>
    [Obsolete("Use Trading:Cash:Seeds[] for static opening balances or Trading:Cash:SignupInitialBalance for self-service signup defaults. Margin.Initial is the transition fallback only and will be removed in a follow-up to #107.")]
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

    /// <summary>
    /// Slice of #108. When <c>true</c>, Market orders are rejected
    /// unless the reference-price lookup returns
    /// <see cref="ReferencePriceSource.Live"/> — i.e. the live MD feed
    /// has a fresh sample under the configured <c>MaxStaleness</c>.
    /// Static config (<see cref="ReferencePriceSource.Fallback"/>) and
    /// missing readings both reject. Limit orders bypass — they carry
    /// their own price, and PriceCollar already handles the staleness
    /// consequence on the band side. Default semantics when unset
    /// everywhere in the precedence chain are conservative: Market
    /// **requires** live MD (see
    /// <see cref="Risk.Checks.StaleReferencePriceCheck"/>). Set to
    /// <c>false</c> per-firm or per-end-client to opt out (e.g. test
    /// accounts that legitimately route Market into the fallback path).
    /// </summary>
    public bool? MarketRequiresLiveRef { get; set; }

    /// <summary>
    /// Slice of #108. Optional whitelist of <see cref="OrderType"/>
    /// values the resolved scope may submit. Case-insensitive enum
    /// names (e.g. <c>"Limit"</c>, <c>"Market"</c>). <c>null</c> or
    /// empty means "no whitelist — every type permitted by the venue
    /// passes". When non-empty, a submission whose type is missing
    /// from the list is rejected with <c>order_type_blocked</c>.
    /// Useful for compliance scopes that should only ever use Limit,
    /// or for staged rollouts of a new order type.
    /// </summary>
    public List<string>? AllowedOrderTypes { get; set; }

    /// <summary>
    /// #433 P1. Venue-side STP instruction sent on every outbound
    /// <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c> (B3
    /// EntryPoint SDK ≥ 0.15.0). Belt-and-braces companion to the
    /// pre-trade <see cref="Checks.SelfTradePreventionCheck"/>: the
    /// pre-trade check still rejects same-firm same-owner crosses
    /// synchronously when <see cref="AllowSelfTrade"/> is not
    /// <c>true</c>; this field tells the matching engine what to do
    /// if a cross still reaches the book (e.g. same-firm
    /// different-owner pairs that the pre-trade check intentionally
    /// allows, or any pair the platform missed because the
    /// working-order snapshot was stale). Default semantics when
    /// unset everywhere in the precedence chain are
    /// <see cref="SelfTradePreventionMode.None"/> — the venue does
    /// not enforce STP unless an operator explicitly opts in per-firm
    /// or per-end-client. The app-side
    /// <see cref="AllowSelfTrade"/> pre-trade check remains the
    /// primary line of defense; this field exists as an opt-in
    /// belt-and-braces enforcement at the matching engine for scopes
    /// that want it. Defaulting to <c>None</c> avoids changing
    /// historical venue behavior for tenants that already rely on
    /// crossed trades reaching the book (e.g. cross-account hedging
    /// inside the same firm).
    /// </summary>
    public SelfTradePreventionMode? SelfTradePreventionMode { get; set; }

    /// <summary>
    /// #473 (SDK 0.15.0). Optional whitelist of
    /// <see cref="Routing.RoutingInstruction"/> values the resolved
    /// scope may stamp on outbound orders. Case-insensitive enum
    /// names (e.g. <c>"RetailLiquidityTaker"</c>, <c>"BrokerOnly"</c>).
    /// <c>null</c> or empty = <b>no instruction permitted</b> (the
    /// resolved value, if any, is rejected pre-trade). Non-empty =
    /// only the listed values are permitted; anything else is
    /// rejected with <c>routing_instruction_blocked</c>.
    ///
    /// <para>
    /// Default is intentionally restrictive (null → block) so a
    /// misconfigured <see cref="Routing.IRoutingInstructionResolver"/>
    /// cannot ship a routing instruction past the risk pipeline by
    /// accident. Operators that want to use any value must explicitly
    /// opt in per scope. Stamps of <c>BrokerOnly</c> are additionally
    /// metric-tagged for audit (conflict-of-interest sensitive).
    /// </para>
    /// </summary>
    public List<string>? AllowedRoutingInstructions { get; set; }
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
    /// Reference-type variant of <see cref="Resolve{T}"/> for limits whose
    /// "unset" sentinel is <c>null</c> at the class-typed level (e.g.
    /// <see cref="RiskLimits.AllowedOrderTypes"/>). Same precedence
    /// chain; first non-null value wins. <paramref name="isSet"/> lets
    /// callers treat empty collections as "not configured" (the typical
    /// shape coming out of a JSON binder that materialises every key
    /// even when the operator left the array blank).
    /// </summary>
    public static T? ResolveRef<T>(
        RiskOptions opts,
        string endClient,
        string? firmId,
        string symbol,
        Func<RiskLimits, T?> selector,
        Func<T, bool>? isSet = null)
        where T : class
    {
        bool Set(T? v) => v is not null && (isSet is null || isSet(v));

        if (opts.PerEndClient.TryGetValue(endClient, out var ec))
        {
            var v = selector(ec);
            if (Set(v)) return v;
        }
        if (!string.IsNullOrWhiteSpace(firmId)
            && opts.PerFirm.TryGetValue(firmId, out var fi))
        {
            var v = selector(fi);
            if (Set(v)) return v;
        }
        if (opts.PerSymbol.TryGetValue(symbol, out var sy))
        {
            var v = selector(sy);
            if (Set(v)) return v;
        }
        var def = selector(opts.Default);
        return Set(def) ? def : null;
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
            MarketRequiresLiveRef = Resolve(opts, endClient, firmId, symbol, l => l.MarketRequiresLiveRef),
            AllowedOrderTypes = ResolveRef(opts, endClient, firmId, symbol,
                l => l.AllowedOrderTypes,
                v => v.Count > 0),
            SelfTradePreventionMode = Resolve(opts, endClient, firmId, symbol, l => l.SelfTradePreventionMode),
            AllowedRoutingInstructions = ResolveRef(opts, endClient, firmId, symbol,
                l => l.AllowedRoutingInstructions,
                v => v.Count > 0),
        };

    /// <summary>
    /// #433 P1. Resolve the venue-side STP mode for a single order,
    /// applying the project-wide default of
    /// <see cref="SelfTradePreventionMode.None"/>
    /// when no scope in the precedence chain pins a value. Returns a
    /// non-nullable value so the gateway never has to make the
    /// "what does null mean" decision at submit time. Defaulting to
    /// <c>None</c> preserves existing venue behavior — operators
    /// opt-in to matching-engine STP enforcement per-firm /
    /// per-end-client.
    /// </summary>
    public static SelfTradePreventionMode ResolveSelfTradePreventionMode(
        RiskOptions opts, string endClient, string? firmId, string symbol) =>
        Resolve(opts, endClient, firmId, symbol, l => l.SelfTradePreventionMode)
            ?? SelfTradePreventionMode.None;
}
