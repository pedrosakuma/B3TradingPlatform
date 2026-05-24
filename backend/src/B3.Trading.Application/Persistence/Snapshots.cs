namespace B3.Trading.Application.Persistence;

/// <summary>
/// Plain DTOs used to serialise the in-memory state of the platform to a
/// snapshot file. All collections are materialised lists (snapshot is a
/// point-in-time photograph) rather than lazy enumerables, so the captured
/// state is independent of subsequent mutations.
///
/// <para>
/// Q4.2 (#302). <b>Multi-firm snapshot shape</b>: the platform takes a
/// SINGLE global snapshot per host with <c>FirmId</c> carried as a
/// dimension on every owner-keyed entry (orders, positions, P&amp;L
/// basis, ownership, sub-accounts, algos…), rather than emitting one
/// file per firm. Rationale:
/// <list type="bullet">
///   <item>One <c>dispatcher.WithSnapshotLock</c> capture gives a
///         consistent cut across every firm at the same Seq —
///         splitting per firm would either need N independent locks
///         (consistency hole) or N reads of the same global lock
///         (no win).</item>
///   <item>WAL is a single global stream by design (#137 / #260).
///         Snapshot+tail recovery only works if snapshot and WAL share
///         the same Seq boundary; one snapshot per firm would have to
///         tail-replay the SAME WAL N times.</item>
///   <item>Sub-account (#316) already chose this shape; multi-firm
///         (#302) follows the established convention so the operator
///         doesn't carry two mental models.</item>
/// </list>
/// Firm-scoped reads (REST, WS, recovery filters) project the slice
/// they need from the global structure — see e.g.
/// <c>PositionKeeper.ForEndClientAndFirm</c>,
/// <c>WorkingOrderBook.ForEndClientAndFirm</c>,
/// <c>PnlProjection.Build(owner, firmId, …)</c>.
/// </para>
/// </summary>
public sealed class PlatformSnapshot
{
    public long Seq { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public List<OrderSnapshot> WorkingOrders { get; init; } = new();
    public List<PositionSnapshot> Positions { get; init; } = new();
    public List<string> KilledEndClients { get; init; } = new();
    public List<string> KilledFirms { get; init; } = new();
    /// <summary>
    /// Symbols currently trading-halted via <c>SymbolHaltService</c>.
    /// Added in the symbol-halt slice; older snapshots that pre-date
    /// the field deserialise with an empty list, which matches the
    /// "no halts" semantics they actually carried.
    /// </summary>
    public List<string> HaltedSymbols { get; init; } = new();

    /// <summary>
    /// Halted symbols with their origin bitmask
    /// (<see cref="B3.Trading.Application.Risk.SymbolHaltEntry.Flags"/>:
    /// Operator=1, Venue=2). Added in #370 Stage A; older snapshots
    /// pre-date the field and deserialise empty, in which case the
    /// snapshotter falls back to <see cref="HaltedSymbols"/> and
    /// treats every entry as an operator halt (the only origin that
    /// existed pre-#370).
    /// </summary>
    public List<SymbolHaltOriginSnapshot> HaltedSymbolOrigins { get; init; } = new();

    /// <summary>
    /// Global default <see cref="B3.Trading.Domain.SessionPhase"/>
    /// (#108) — applied when a symbol has no override. Older snapshots
    /// pre-date the field and deserialise as <c>"Continuous"</c>, which
    /// matches the implicit "trading is on" semantics they carried.
    /// Stored as the enum name to keep the wire format stable across
    /// future enum renumberings.
    /// </summary>
    public string DefaultSessionPhase { get; init; } = "Continuous";

    /// <summary>
    /// Per-symbol session-phase overrides (#108). Empty on snapshots
    /// pre-dating the field, which collapses to "everything follows
    /// the default" — same behaviour as before the slice existed.
    /// </summary>
    public List<SessionPhaseOverrideSnapshot> SessionPhaseOverrides { get; init; } = new();
    public ClOrdIdRegistrySnapshot ClOrdIds { get; init; } = new();
    public List<OwnershipMappingSnapshot> Ownership { get; init; } = new();
    public List<AlgoSnapshot> Algos { get; init; } = new();
    public AlgoIdRegistrySnapshot AlgoIds { get; init; } = new();
    /// <summary>
    /// Per-end-client cash balances captured by <c>CashLedger</c>.
    /// Added in the cash-balance slice (#107 slice 1); older
    /// snapshots that pre-date the field deserialise with an empty
    /// list, which matches the "no balance recorded" semantics they
    /// actually carried (callers see zero until the seed re-applies
    /// or a fill arrives).
    /// </summary>
    public List<CashBalanceSnapshot> CashBalances { get; init; } = new();

    /// <summary>
    /// Q2.2 (#269). Per-end-client cash balances projected from
    /// <c>CashLedgerEvent</c> (operator deposits/withdrawals) by
    /// <c>CashKeeper</c>. Additive field; older snapshots that pre-date
    /// it deserialise with an empty dictionary, which matches the
    /// "no operator activity recorded" semantics they actually carried.
    /// Init-only and default-empty so callers cannot accidentally null
    /// it out post-deserialisation. Dict (not list) by spec — keyed on
    /// end-client id, value is the running balance.
    /// </summary>
    public Dictionary<string, decimal> CashByEndclient { get; init; } = new();

    /// <summary>
    /// Q2.3 (#270). Per-(end-client, day) running fee totals projected
    /// from the <c>FeeAccruedEvent</c> stream by <c>FeeKeeper</c>. Key
    /// is <c>{endClientId}|{yyyy-MM-dd}</c> (see
    /// <c>FeeKeeper.FormatKey</c>); value is the running BRL total.
    /// Additive field; older snapshots that pre-date it deserialise
    /// with an empty dictionary, which matches the "no fees recorded"
    /// semantics those snapshots actually carried (the tail's
    /// <c>FeeAccruedEvent</c> stream then rebuilds the running totals
    /// from scratch).
    /// </summary>
    public Dictionary<string, decimal> FeesByEndclientDay { get; init; } = new();

    /// <summary>
    /// Q2.3 (#270). Idempotence guard for the fee projection — the set
    /// of <c>FeeAccruedEvent.ExecutionId</c> values that have already
    /// been folded into <see cref="FeesByEndclientDay"/>. Persisted so
    /// a snapshot+tail recovery does not double-count the events whose
    /// effect is already baked into the snapshot's totals. Empty on
    /// older snapshots that pre-date the field, which collapses to
    /// "rebuild from the WAL alone" — also safe because the tail then
    /// contains every event since seq 0.
    /// </summary>
    public List<string> FeeSeenExecutionIds { get; init; } = new();

    /// <summary>
    /// Q2.4 (#271). Per-(end-client, symbol, day) realized-P&amp;L
    /// running totals projected from the <c>RealizedPnlEvent</c> stream
    /// by <c>PnlKeeper</c>. Key is
    /// <c>{endClientId}|{symbol}|{yyyy-MM-dd}</c> (see
    /// <c>PnlKeeper.FormatRealizedKey</c>); value is the running BRL
    /// realized total. Empty on snapshots pre-dating the field —
    /// rebuilt from the WAL alone in that case.
    /// </summary>
    public Dictionary<string, decimal> PnlRealizedByEndclientSymbolDay { get; init; } = new();

    /// <summary>
    /// Q2.4 (#271). Per-(end-client, symbol) avg-cost basis tracked by
    /// <c>PnlKeeper</c> in parallel with <see cref="Positions"/>. Empty
    /// on older snapshots; rebuilt from <see cref="Positions"/> by the
    /// recovery path (<c>PersistenceRecovery</c>) when missing.
    /// </summary>
    public List<PnlAvgCostSnapshot> PnlAvgCost { get; init; } = new();

    /// <summary>
    /// Pass-3 review (#278) P1. Per-(end-client, symbol) "unknown
    /// basis" qty rows seeded from a legacy <c>Positions</c> row whose
    /// <c>AverageEntryPrice</c> was zero. Empty on snapshots
    /// pre-dating the field; recovery code re-derives the set from
    /// <see cref="Positions"/> in that case via
    /// <c>PnlKeeper.SeedAvgCostFromLegacyPositions</c>.
    /// </summary>
    public List<PnlUnknownBasisSnapshot> PnlUnknownBasis { get; init; } = new();

    /// <summary>
    /// Q2.4 (#271). Idempotence guard for the realized-P&amp;L
    /// projection — the set of <c>RealizedPnlEvent.ExecutionId</c>
    /// values already folded into <see cref="PnlRealizedByEndclientSymbolDay"/>.
    /// </summary>
    public List<string> PnlSeenExecutionIds { get; init; } = new();

    /// <summary>
    /// User-issued bot credentials (sub-issue #169). Empty on snapshots
    /// pre-dating the field — credentials are reconstructed instead by
    /// the WAL replay path on top of the empty list, which matches the
    /// "no PATs minted" semantics those snapshots actually carried.
    /// </summary>
    public List<UserBotCredentialSnapshot> UserBotCredentials { get; init; } = new();

    /// <summary>
    /// Per-credential FIXP session state (sub-issue #170 / RFC §4.8
    /// "Snapshot scope"). Empty on snapshots pre-dating the field — the
    /// state is then reconstructed by WAL replay of
    /// <c>BotSessionInitializedEvent</c> +
    /// <c>BotSessionVerAdvancedEvent</c>, which yields the same shape.
    /// </summary>
    public List<BotSessionStateSnapshot> BotSessions { get; init; } = new();

    /// <summary>
    /// Sub-issue #171 (E). FIXP order mappings — one entry per live
    /// (non-reaped) bot-origin order, keyed by internal ClOrdID. Empty
    /// on snapshots pre-dating the field; reconstructed on replay from
    /// <see cref="OrderSubmittedEvent.BotMapping"/> entries past the
    /// snapshot seq.
    /// </summary>
    public List<BotOrderMappingSnapshot> BotOrderMappings { get; init; } = new();

    /// <summary>
    /// Sub-issue #171 (E). FIXP cancel-side mappings — one entry per
    /// in-flight bot-origin cancel keyed by cancel-side internal ClOrdID,
    /// pointing at both the original internal ClOrdID and the bot's
    /// external cancel ClOrdID. Empty on snapshots pre-dating the field.
    /// </summary>
    public List<BotCancelMappingSnapshot> BotCancelMappings { get; init; } = new();

    /// <summary>
    /// Pass-4 review (#255). ClOrdIds whose <c>OrderExpiredEvent</c>
    /// audit envelope is durably on the WAL but whose downstream
    /// <c>OrderCancelRequestedEvent</c> has not yet been observed.
    /// Captured under the dispatcher lock so the persisted set is
    /// consistent with the snapshot's <c>Seq</c>: when a snapshot is
    /// taken between the audit append and the cancel append, this set
    /// records that the audit is already on disk so the post-restart
    /// timer fire (which sees an order still in working state in the
    /// snapshot) does not emit a duplicate audit envelope.
    /// <para>
    /// Empty on snapshots pre-dating the field, which collapses to the
    /// pre-pass-4 behaviour: <c>EventReplayer.Apply(OrderExpiredEvent)</c>
    /// is then the only writer. The bug pass-4 fixes is the small
    /// window where <c>EventReplayer</c> never sees the audit because
    /// its seq is &lt;= snapshot.Seq.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<ulong> AuditedExpiredIds { get; init; } = Array.Empty<ulong>();

    /// <summary>
    /// Pass-1 review (#295) P1#1. Persisted per-POV scheduling progress
    /// — <c>(firmId, algoId) → (marketVolumeSeen, lastEvaluateAtUtc)</c>.
    /// Without this baseline a restart would have <see cref="B3.Trading.Application.Algo"/>'s
    /// POV path re-derive the cumulative market volume from
    /// <c>VolumeCurveEstimator</c>'s in-memory buckets, which lost
    /// the pre-crash trade history; the parent would then under-slice
    /// until post-restart volume catches up to the already-executed
    /// cumulative. Empty on snapshots pre-dating the field, which
    /// collapses to the pre-fix behaviour (engine seeds progress from
    /// <c>StartUtc</c> on first tick — same as a fresh POV).
    /// </summary>
    public List<PovProgressSnapshot> PovProgress { get; init; } = new();

    /// <summary>
    /// Pass-1 review (#296) P1-C. In-flight Pegged repeg-cycle
    /// markers — engine emitted the cancel for a drift-driven repeg
    /// but had not yet observed the cancel-ack ER + submitted the
    /// replacement at snapshot capture. Empty on snapshots
    /// pre-dating the field (additive); the engine treats absence as
    /// "no pending cycle" — same as a fresh Pegged parent. See
    /// <see cref="B3.Trading.Application.PeggedRepegBook"/>.
    /// </summary>
    public List<PeggedRepegPendingSnapshot> PeggedRepegPending { get; init; } = new();

    /// <summary>
    /// Pass-5 review (#296) P1. Per-Pegged-parent FIFO of recently
    /// engine-cancelled child clOrdIds — late-ER dedup memory. Empty
    /// on snapshots pre-dating the field (additive); the engine
    /// treats absence the same as a freshly-started process. See
    /// <see cref="B3.Trading.Application.PeggedRepegBook.MarkCancelledChild"/>.
    /// </summary>
    public List<PeggedRepegHistorySnapshot> PeggedRepegHistory { get; init; } = new();

    /// <summary>
    /// Pass-6 review (#299) P1. Persisted in-flight cancel-replace
    /// registry — every <see cref="B3.Trading.Application.PendingReplacementRegistry"/>
    /// entry, including the ambiguous-margin-held flag,
    /// <c>HeldAtUtc</c>, and <c>NewRemainingNotional</c>. Required so a
    /// periodic snapshot taken AFTER an
    /// <see cref="OrderReplaceAmbiguousMarginHeldEvent"/> survives
    /// recovery whose WAL tail starts past that event — without this,
    /// the post-restart sweep would have no ambiguous entry and a late
    /// Replaced/Rejected ER would miss the registry path entirely
    /// (over-allocation invariant break). Empty on snapshots
    /// pre-dating the field (additive); the restore path treats
    /// absence as "no in-flight modifies — same as a freshly-started
    /// process".
    /// </summary>
    public List<PendingReplacementSnapshot> PendingReplacements { get; init; } = new();

    /// <summary>
    /// Q4.1 (#301). Per-firm registry of known sub-accounts (active
    /// + soft-deleted). Empty on snapshots pre-dating the field;
    /// rebuilt entirely from <see cref="SubAccountCreatedEvent"/> /
    /// <see cref="SubAccountDeactivatedEvent"/> replay in that case.
    /// </summary>
    public List<SubAccountSnapshot> SubAccounts { get; init; } = new();

    /// <summary>
    /// Q4.1 (#301). Per-(end-client, sub-account, symbol) net
    /// positions for sub-account-tagged fills. The master aggregate
    /// continues to live in <see cref="Positions"/> (sub-account-null
    /// and sub-account-tagged fills both folded). Empty on snapshots
    /// pre-dating the field.
    /// </summary>
    public List<SubAccountPositionSnapshot> SubAccountPositions { get; init; } = new();

    /// <summary>
    /// Q4.1 (#301). Per-(end-client, sub-account, symbol, day)
    /// realized-P&amp;L running totals. Empty on snapshots
    /// pre-dating the field.
    /// </summary>
    public List<SubAccountPnlSnapshot> SubAccountPnl { get; init; } = new();

    /// <summary>
    /// PR #316 P2. Per-bucket avg-cost basis rows powering
    /// <see cref="B3.Trading.Application.SubAccountPnlKeeper.ApplyBucketFill"/>.
    /// Empty on snapshots predating the field — legacy hydrates to an
    /// empty basis map (best-effort, see record doc).
    /// </summary>
    public List<SubAccountPnlBasisSnapshot> SubAccountPnlBasis { get; init; } = new();

    /// <summary>
    /// #380 path B. Per-firm last-observed FIXP <c>SessionVerId</c> at
    /// snapshot capture time, sourced from each
    /// <see cref="B3.Trading.Infrastructure.IFirmSessionStatusProvider"/>
    /// entry. Empty on snapshots pre-dating the field (additive). When
    /// present, <see cref="B3.Trading.Infrastructure.Persistence.PersistenceRecovery"/>
    /// compares each firm's current gateway <c>SessionVerId</c> against
    /// the stored value after replay and eagerly retires every
    /// <see cref="B3.Trading.Application.WorkingOrderBook"/> entry
    /// attached to a firm whose verId has advanced past the snapshot
    /// (the venue rolled the session — orders attached to the prior
    /// version are ghosts and would otherwise poison STP, modify, and
    /// margin checks until the next ER ack that never comes).
    /// Restore is a no-op for the snapshot itself; the dict is read
    /// straight off the loaded snapshot by the recovery path.
    /// </summary>
    public Dictionary<string, uint> FirmSessionVerIds { get; init; } = new();
}

/// <summary>
/// Sub-issue #171 (E). One row of the
/// <c>internalClOrdId → (credentialId, externalClOrdId)</c> bot-origin
/// order map.
/// </summary>
public sealed record BotOrderMappingSnapshot(
    ulong InternalClOrdId,
    Guid CredentialId,
    ulong ExternalClOrdId);

/// <summary>
/// Sub-issue #171 (E). One row of the bot-origin cancel-side map. The
/// cancel's internal ClOrdID resolves to both the original internal
/// ClOrdID (so cancel-ack ER routing can find the original order) and
/// the bot's external cancel ClOrdID (so F can echo it back to the bot).
/// </summary>
public sealed record BotCancelMappingSnapshot(
    ulong CancelInternalClOrdId,
    ulong OriginalInternalClOrdId,
    Guid CredentialId,
    ulong ExternalCancelClOrdId);

/// <summary>
/// Captures one per-credential FIXP session row. The active-connection
/// slot is intentionally <b>not</b> persisted — single-active enforcement
/// is per-process and any in-flight TCP connection is gone after a
/// restart anyway.
/// </summary>
public sealed record BotSessionStateSnapshot(
    Guid CredentialId,
    uint SessionId,
    ulong CurrentVer,
    ulong LastCheckpointedOutboundSeq);

/// <summary>
/// Captures one row from <c>InMemoryUserBotCredentialRegistry</c>.
/// Mirrors the <c>UserBotCredential</c> record one-to-one; the secret
/// is stored only as the bcrypt hash (never plaintext).
/// </summary>
public sealed record UserBotCredentialSnapshot(
    Guid Id,
    string UserId,
    string CredShortId,
    string Label,
    string SecretHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record OrderSnapshot(
    ulong ClOrdId,
    string EndClientId,
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    decimal? Price,
    long LeavesQuantity,
    long CumulativeQuantity,
    string Status,
    string FirmId = "DEFAULT",
    ulong? ParentAlgoId = null,
    int? AlgoSliceSeq = null)
{
    /// <summary>
    /// Slice 1 of #132. Advisory venue-staleness overlay. Older snapshots
    /// pre-date the field; default of <c>false</c> is correct on restore
    /// (no stale marks before this slice existed).
    /// </summary>
    public bool IsStale { get; init; }

    public string? StaleReason { get; init; }

    public DateTimeOffset? StaledAtUtc { get; init; }

    /// <summary>
    /// Q1.1 (#253). Time-in-force at submit time. Defaults to
    /// <c>"Day"</c> so older snapshots without the field hydrate with
    /// the implicit "Day" semantics they actually carried.
    /// </summary>
    public string TimeInForce { get; init; } = nameof(B3.Trading.Domain.TimeInForce.Day);

    /// <summary>
    /// Q1.1 (#253). Trigger price for StopLoss/StopLimit; <c>null</c>
    /// otherwise. Older snapshots default to <c>null</c>.
    /// </summary>
    public decimal? StopPrice { get; init; }

    /// <summary>
    /// Q1.1 (#253). Expiry timestamp for GTD; <c>null</c> otherwise.
    /// Older snapshots default to <c>null</c>.
    /// </summary>
    public DateTimeOffset? GoodTillDate { get; init; }

    /// <summary>
    /// Q3.4 (#284). Native iceberg / reserve display quantity at
    /// submit time. Null = full disclosure. Older snapshots default
    /// to <c>null</c>.
    /// </summary>
    public long? DisplayQty { get; init; }

    /// <summary>
    /// Q3.4 (#284). Refresh policy for the visible portion of an
    /// iceberg order, stored as the enum name. Null iff
    /// <see cref="DisplayQty"/> is null. Older snapshots default
    /// to <c>null</c>.
    /// </summary>
    public string? DisplayResetPolicy { get; init; }

    /// <summary>
    /// Q4.1 (#301). Optional sub-account bucket the order was booked
    /// against at submit time. Null = master bucket. Older snapshots
    /// without the field hydrate as null, matching the pre-#301
    /// semantics they actually carried.
    /// </summary>
    public string? SubAccountId { get; init; }

    /// <summary>
    /// #457. Minimum execution quantity (FIX MinQty) at submit time.
    /// Null = no minimum. Older snapshots default to <c>null</c>,
    /// matching the pre-#457 no-minimum semantics.
    /// </summary>
    public long? MinQty { get; init; }
}

/// <summary>
/// Captures an <see cref="B3.Trading.Domain.Algo"/> aggregate. Discriminated
/// per-type fields mirror the <see cref="AlgoCreatedEvent"/> wire shape so
/// that recovery via WAL-only and recovery via snapshot+tail produce
/// byte-identical aggregates.
/// </summary>
public sealed record AlgoSnapshot(
    ulong AlgoId,
    string EndClientId,
    string FirmId,
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long TotalQuantity,
    long FilledQuantity,
    string Status,
    string TerminalReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    long? IcebergDisplayQuantity = null,
    decimal? IcebergLimitPrice = null,
    DateTimeOffset? TwapStartUtc = null,
    DateTimeOffset? TwapEndUtc = null,
    int? TwapSliceCount = null,
    string? TwapChildOrderType = null,
    decimal? TwapChildPrice = null,
    // Q3.1 (#281) — VWAP fields, mirror the TWAP block.
    DateTimeOffset? VwapStartUtc = null,
    DateTimeOffset? VwapEndUtc = null,
    string? VwapChildOrderType = null,
    decimal? VwapChildPrice = null,
    long? VwapTickIntervalTicks = null,
    decimal? VwapSliceMaxPct = null,
    decimal? VwapPriceLimit = null,
    decimal? VwapParticipationCap = null,
    // Q3.2 (#282) — POV fields, mirror the VWAP block.
    DateTimeOffset? PovStartUtc = null,
    DateTimeOffset? PovEndUtc = null,
    string? PovChildOrderType = null,
    decimal? PovChildPrice = null,
    decimal? PovParticipationRate = null,
    long? PovTickIntervalTicks = null,
    decimal? PovPriceLimit = null,
    long? PovMinSliceQty = null,
    // Q3.3 (#283) — Pegged fields, mirror the POV block.
    string? PeggedRef = null,
    int? PeggedOffsetTicks = null,
    long? PeggedRepegIntervalTicks = null,
    decimal? PeggedTickSize = null,
    string? PeggedChildOrderType = null,
    decimal? PeggedPriceLimit = null);

public sealed record PositionSnapshot(
    string EndClientId,
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice,
    // PR #316 P1. Firm dimension on owner-keyed state. Defaults to
    // "default" so older snapshots and test-only call sites that
    // construct PositionSnapshot positionally still round-trip; the
    // firm-aware code paths overwrite this with the real firm.
    string FirmId = "DEFAULT");

public sealed record CashBalanceSnapshot(
    string EndClientId,
    decimal Available);

/// <summary>
/// Q2.4 (#271). Avg-cost basis row persisted alongside
/// <see cref="PlatformSnapshot.PnlRealizedByEndclientSymbolDay"/>.
/// </summary>
public sealed record PnlAvgCostSnapshot(
    string EndClientId,
    string Symbol,
    long NetQuantity,
    decimal AvgPrice,
    // PR #316 P1. Firm dimension on owner-keyed state. Defaults to
    // "default" so older snapshots hydrate as a single legacy bucket.
    string FirmId = "DEFAULT");

/// <summary>
/// Pass-3 review (#278) P1. Persisted "unknown basis" qty row —
/// see <see cref="PlatformSnapshot.PnlUnknownBasis"/>.
/// </summary>
public sealed record PnlUnknownBasisSnapshot(
    string EndClientId,
    string Symbol,
    long NetQuantity,
    // PR #316 P1. Firm dimension on owner-keyed state. Defaults to
    // "default" so older snapshots hydrate as a single legacy bucket.
    string FirmId = "DEFAULT");

/// <summary>
/// Pass-1 review (#295) P1#1. One row of the per-POV scheduling-progress
/// projection — see <see cref="PlatformSnapshot.PovProgress"/>.
/// </summary>
public sealed record PovProgressSnapshot(
    string FirmId,
    ulong AlgoId,
    long MarketVolumeSeen,
    DateTimeOffset LastEvaluateAtUtc);

/// <summary>
/// Pass-1 review (#296) P1-C. One row of the per-Pegged in-flight
/// repeg-cycle projection — see
/// <see cref="PlatformSnapshot.PeggedRepegPending"/>.
/// </summary>
public sealed record PeggedRepegPendingSnapshot(
    string FirmId,
    ulong AlgoId,
    ulong CancelledChildClOrdId,
    decimal TargetPrice,
    DateTimeOffset AtUtc);

/// <summary>
/// Pass-5 review (#296) P1. One row of the per-Pegged cancelled-child
/// history — see <see cref="PlatformSnapshot.PeggedRepegHistory"/>.
/// <see cref="ChildClOrdIds"/> is FIFO oldest→newest.
/// <para>
/// Pass-7 review (#296) P2. <see cref="EvictionLogged"/> persists the
/// per-ring one-shot "we've already warn-logged about FIFO overflow on
/// this parent" latch so a restart does not let the warn re-fire on
/// the next eviction. Optional (default <c>false</c>); snapshots
/// pre-dating the field round-trip to a fresh latch — at worst one
/// extra warn post-upgrade.
/// </para>
/// </summary>
public sealed record PeggedRepegHistorySnapshot(
    string FirmId,
    ulong AlgoId,
    List<ulong> ChildClOrdIds,
    bool EvictionLogged = false);

/// <summary>
/// Pass-6 review (#299) P1. Persisted shape of one
/// <see cref="B3.Trading.Application.PendingReplacementRegistry"/>
/// entry — see <see cref="PlatformSnapshot.PendingReplacements"/>.
/// Carries every field the restore path needs to re-hydrate the
/// in-flight intent AND (when <see cref="AmbiguousMarginHeld"/> is
/// <c>true</c>) re-invoke
/// <c>IReplaceMarginCoordinator.PrepareReplaceAsync</c> with the
/// same value the pre-crash dispatch used.
/// </summary>
public sealed record PendingReplacementSnapshot(
    ulong OriginalClOrdId,
    ulong NewClOrdId,
    string OwnerEndClientId,
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long NewQuantity,
    decimal? NewPrice,
    string FirmId,
    ulong? ParentAlgoId,
    int? AlgoSliceSeq,
    string? RequestedTimeInForce,
    decimal? RequestedStopPrice,
    DateTimeOffset? RequestedGoodTillDate,
    DateTimeOffset CreatedAtUtc,
    bool AmbiguousMarginHeld,
    DateTimeOffset? AmbiguousAtUtc,
    decimal NewRemainingNotional);

public sealed class ClOrdIdRegistrySnapshot
{
    public long NextPrefix { get; init; }
    public List<ClOrdIdCounterSnapshot> Counters { get; init; } = new();
}

public sealed record ClOrdIdCounterSnapshot(string EndClientId, ulong PrefixIdx, long Counter);

public sealed record OwnershipMappingSnapshot(ulong ClOrdId, string EndClientId);

/// <summary>
/// Per-firm <c>AlgoId</c> counters. Mirrors the firm-isolation pattern
/// of every other persisted aggregate; reset to <c>0</c> for an unknown
/// firm on restore (a never-seen-before firm starts at <c>1</c>).
/// </summary>
public sealed class AlgoIdRegistrySnapshot
{
    public List<AlgoIdCounterSnapshot> Counters { get; init; } = new();
}

public sealed record AlgoIdCounterSnapshot(string FirmId, long Counter);

/// <summary>
/// One per-symbol session-phase override (#108). Phase stored as the
/// enum name for forward-compat across reorderings.
/// </summary>
public sealed record SessionPhaseOverrideSnapshot(string Symbol, string Phase);

/// <summary>
/// A halted symbol with its origin bitmask (Operator=1, Venue=2,
/// both=3). Wire form for <see cref="PlatformSnapshot.HaltedSymbolOrigins"/>.
/// Added by #370 Stage A.
/// </summary>
public sealed record SymbolHaltOriginSnapshot(string Symbol, byte Flags);

/// <summary>
/// Q4.1 (#301). One per-firm row of the
/// <see cref="B3.Trading.Application.SubAccountsRegistry"/>.
/// <see cref="Active"/> = false means soft-deleted: orders still
/// resolve historically, but the submit pipeline rejects new ones
/// (the registry's <c>IsActive</c> gate fails).
/// </summary>
public sealed record SubAccountSnapshot(string FirmId, string Id, string? DisplayName, bool Active);

/// <summary>
/// Q4.1 (#301). One <c>(FirmId, EndClientId, SubAccountId, Symbol) →
/// net quantity + avg price</c> row from
/// <see cref="B3.Trading.Application.SubAccountPositionKeeper"/>.
///
/// <para>
/// <b>Migration note.</b> <see cref="FirmId"/> is additive and
/// non-nullable. The DTO type is new in Q4.1 (#301) — no production
/// snapshots predate it, so there are no legacy records to migrate.
/// </para>
/// </summary>
public sealed record SubAccountPositionSnapshot(
    string FirmId,
    string EndClientId,
    string SubAccountId,
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice);

/// <summary>
/// Q4.1 (#301). One <c>(FirmId, EndClientId, SubAccountId, Symbol,
/// Day) → realized total</c> row from
/// <see cref="B3.Trading.Application.SubAccountPnlKeeper"/>.
///
/// <para>
/// <b>Migration note.</b> Same shape concern as
/// <see cref="SubAccountPositionSnapshot"/>: <see cref="FirmId"/> is
/// additive non-nullable on a type new in Q4.1, no pre-merge data
/// exists.
/// </para>
/// </summary>
public sealed record SubAccountPnlSnapshot(
    string FirmId,
    string EndClientId,
    string SubAccountId,
    string Symbol,
    DateOnly Day,
    decimal RealizedTotal);

/// <summary>
/// PR #316 P2. One row of the per-bucket avg-cost basis projected
/// by <see cref="B3.Trading.Application.SubAccountPnlKeeper"/>.
/// <see cref="SubAccountId"/> is
/// <see cref="B3.Trading.Application.SubAccountPnlKeeper.MasterBucketKey"/>
/// (empty string) for the MASTER bucket (fills with no sub-account
/// tag) and a real sub-account id otherwise. Persisted alongside
/// <see cref="SubAccountPnlSnapshot"/> so a snapshot+tail recovery
/// preserves bucket-level basis. Additive on snapshots predating
/// the field; legacy hydrates to an empty basis map (best-effort
/// recovery — see <c>SubAccountPnlKeeper.SnapshotBasis</c> for the
/// recovery semantics).
/// </summary>
public sealed record SubAccountPnlBasisSnapshot(
    string FirmId,
    string EndClientId,
    string SubAccountId,
    string Symbol,
    long NetQuantity,
    decimal AvgPrice);
