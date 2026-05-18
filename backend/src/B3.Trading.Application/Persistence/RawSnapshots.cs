using B3.Trading.Application.UserBots;
using B3.Trading.Domain;

namespace B3.Trading.Application.Persistence;

/// <summary>
/// Raw, lock-side capture of platform state for the two-phase snapshot
/// pipeline (RFC §5.8 / P6). Every per-registry <c>RawSnapshot()</c>
/// returns a pre-sized array of an immutable per-element struct; the
/// arrays are then stitched together into a <see cref="RawPlatformSnapshot"/>
/// by <see cref="StateSnapshotter.CaptureRaw"/> and projected into the
/// persisted <see cref="PlatformSnapshot"/> shape — including all
/// enum-name <c>ToString</c>, sorting, and final <c>List&lt;T&gt;</c>
/// allocation — by <c>StateSnapshotter.Project</c> outside the dispatcher
/// lock.
///
/// <para><b>Snapshot consistency (RFC §4.3).</b> Every mutable scalar
/// read off a domain aggregate (<see cref="Order"/>, <see cref="Algo"/>,
/// <c>Position</c>) is captured into its raw struct AT CALL TIME — i.e.
/// while the caller still holds the dispatcher lock. The projection
/// step never re-reads those fields off the live aggregate, only off
/// the captured raw struct, so no event with <c>seq &gt; snapshot.Seq</c>
/// can leak into the persisted view even though projection runs after
/// the lock is released. Immutable fields (<c>ClOrdId</c>, <c>Symbol</c>,
/// <c>Side</c>, <c>Type</c>, <c>FirmId</c>, …) are read off the
/// captured object reference outside the lock — safe by construction:
/// they are set once at construction and never mutated.</para>
/// </summary>
public sealed class RawPlatformSnapshot
{
    public long Seq { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }

    public OrderRaw[] Orders { get; init; } = Array.Empty<OrderRaw>();
    public AlgoRaw[] Algos { get; init; } = Array.Empty<AlgoRaw>();
    public PositionRaw[] Positions { get; init; } = Array.Empty<PositionRaw>();

    public string[] KilledEndClients { get; init; } = Array.Empty<string>();
    public string[] KilledFirms { get; init; } = Array.Empty<string>();
    public string[] HaltedSymbols { get; init; } = Array.Empty<string>();

    public SessionPhase DefaultPhase { get; init; } = SessionPhase.Continuous;
    public SessionPhaseOverrideRaw[] SessionPhaseOverrides { get; init; } =
        Array.Empty<SessionPhaseOverrideRaw>();

    public ClOrdIdRegistryRaw ClOrdIds { get; init; } = ClOrdIdRegistryRaw.Empty;
    public AlgoIdCounterRaw[] AlgoIds { get; init; } = Array.Empty<AlgoIdCounterRaw>();
    public OwnershipRaw[] Ownership { get; init; } = Array.Empty<OwnershipRaw>();
    public CashRaw[] CashBalances { get; init; } = Array.Empty<CashRaw>();

    /// <summary>
    /// Q2.2 (#269). Per-end-client cash balances projected from the
    /// <c>CashLedgerEvent</c> stream by <c>CashKeeper</c>. Distinct from
    /// <see cref="CashBalances"/> (which is the fill-derived projection
    /// used by the margin pipeline).
    /// </summary>
    public CashKeeperRaw[] CashByEndclient { get; init; } = Array.Empty<CashKeeperRaw>();

    /// <summary>
    /// Q2.3 (#270). Per-(end-client, day) total fees projected from
    /// the <c>FeeAccruedEvent</c> stream by <c>FeeKeeper</c>.
    /// </summary>
    public FeeKeeperRaw[] FeesByEndclientDay { get; init; } = Array.Empty<FeeKeeperRaw>();

    /// <summary>
    /// Q2.3 (#270). Idempotence guard for <c>FeeKeeper.Apply</c> —
    /// captured under the dispatcher lock alongside <see cref="FeesByEndclientDay"/>
    /// so a snapshot+tail recovery dedupes the tail correctly.
    /// </summary>
    public string[] FeeSeenExecutionIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Q2.4 (#271). Per-(end-client, symbol, day) realized P&amp;L
    /// projected from the <c>RealizedPnlEvent</c> stream by
    /// <c>PnlKeeper</c>.
    /// </summary>
    public PnlRealizedRaw[] PnlRealizedByEndclientSymbolDay { get; init; } = Array.Empty<PnlRealizedRaw>();

    /// <summary>
    /// Q2.4 (#271). Per-(end-client, symbol) avg-cost basis tracked
    /// by <c>PnlKeeper</c> in parallel with <see cref="Positions"/>.
    /// Required so the keeper can compute realized deltas after a
    /// snapshot+tail restore even if its peer keepers are wiped.
    /// </summary>
    public PnlAvgCostRaw[] PnlAvgCost { get; init; } = Array.Empty<PnlAvgCostRaw>();

    /// <summary>
    /// Pass-3 review (#278) P1. Per-(end-client, symbol) "unknown
    /// basis" qty rows — legacy positions for which the rehydrated
    /// snapshot carried no usable avg price. Persisted so a
    /// snapshot+tail recovery doesn't re-skip and re-introduce
    /// phantom-P&amp;L on the next restart.
    /// </summary>
    public PnlUnknownBasisRaw[] PnlUnknownBasis { get; init; } = Array.Empty<PnlUnknownBasisRaw>();

    /// <summary>
    /// Q2.4 (#271). Idempotence guard for <c>PnlKeeper.Apply</c>.
    /// </summary>
    public string[] PnlSeenExecutionIds { get; init; } = Array.Empty<string>();

    public UserBotCredential[] UserBotCredentials { get; init; } =
        Array.Empty<UserBotCredential>();
    public BotSessionState[] BotSessions { get; init; } = Array.Empty<BotSessionState>();

    public BotOrderMappingRaw[] BotOrderMappings { get; init; } =
        Array.Empty<BotOrderMappingRaw>();
    public BotCancelMappingRaw[] BotCancelMappings { get; init; } =
        Array.Empty<BotCancelMappingRaw>();

    /// <summary>
    /// Pass-4 review (#255). Mirror of
    /// <c>PlatformSnapshot.AuditedExpiredIds</c> at the raw-capture
    /// stage. Populated under the dispatcher lock from
    /// <c>GtdExpirationScheduler.SnapshotAuditedExpiredIds()</c>.
    /// </summary>
    public ulong[] AuditedExpiredIds { get; init; } = Array.Empty<ulong>();

    /// <summary>
    /// Pass-1 review (#295) P1#1. Mirror of
    /// <c>PlatformSnapshot.PovProgress</c> at the raw-capture stage.
    /// Populated under the dispatcher lock from
    /// <c>PovProgressBook.Snapshot()</c>.
    /// </summary>
    public PovProgressRaw[] PovProgress { get; init; } = Array.Empty<PovProgressRaw>();

    /// <summary>
    /// Pass-1 review (#296) P1-C. Mirror of
    /// <c>PlatformSnapshot.PeggedRepegPending</c> at the raw-capture
    /// stage. Populated under the dispatcher lock from
    /// <c>PeggedRepegBook.Snapshot()</c>. Empty on snapshots
    /// pre-dating the field (additive).
    /// </summary>
    public PeggedRepegPendingRaw[] PeggedRepegPending { get; init; } = Array.Empty<PeggedRepegPendingRaw>();

    /// <summary>
    /// Pass-5 review (#296) P1. Per-Pegged-parent FIFO of recently
    /// engine-cancelled child clOrdIds, used by the late-ER dedup
    /// guards in <see cref="B3.Trading.Application.AlgoEngine"/>.
    /// Empty on snapshots pre-dating the field (additive); engine
    /// treats absence as "no remembered cancels" — equivalent to a
    /// freshly-started process.
    /// </summary>
    public PeggedRepegHistoryRaw[] PeggedRepegHistory { get; init; } = Array.Empty<PeggedRepegHistoryRaw>();

    /// <summary>
    /// Pass-6 review (#299) P1. Mirror of
    /// <c>PlatformSnapshot.PendingReplacements</c> at the raw-capture
    /// stage. Carries every <see cref="PendingReplacementRegistry"/>
    /// entry — including its <see cref="PendingReplacementRaw.AmbiguousMarginHeld"/>
    /// flag, <see cref="PendingReplacementRaw.AmbiguousAt"/>, and
    /// <see cref="PendingReplacementRaw.NewRemainingNotional"/> — so a
    /// snapshot taken AFTER an ambiguous mark survives recovery even
    /// when the WAL tail starts past the matching
    /// <c>OrderReplaceAmbiguousMarginHeldEvent</c>. Empty on snapshots
    /// pre-dating the field (additive — legacy snapshots restore as a
    /// no-op, matching pre-pass-6 behaviour).
    /// </summary>
    public PendingReplacementRaw[] PendingReplacements { get; init; } = Array.Empty<PendingReplacementRaw>();

    /// <summary>
    /// Q4.1 (#301). Per-firm registry of sub-accounts. Empty on
    /// snapshots pre-dating the field (additive); rebuilt entirely
    /// from <c>SubAccountCreatedEvent</c> / <c>SubAccountDeactivatedEvent</c>
    /// replay in that case.
    /// </summary>
    public IReadOnlyList<SubAccountSnapshot> SubAccounts { get; init; } =
        Array.Empty<SubAccountSnapshot>();

    /// <summary>
    /// Q4.1 (#301). Per-(end-client, sub-account, symbol) net positions.
    /// </summary>
    public IReadOnlyList<SubAccountPositionSnapshot> SubAccountPositions { get; init; } =
        Array.Empty<SubAccountPositionSnapshot>();

    /// <summary>
    /// Q4.1 (#301). Per-(end-client, sub-account, symbol, day) realized P&amp;L totals.
    /// </summary>
    public IReadOnlyList<SubAccountPnlSnapshot> SubAccountPnl { get; init; } =
        Array.Empty<SubAccountPnlSnapshot>();
}

/// <summary>
/// Pass-1 review (#296) P1-C. Raw per-Pegged in-flight repeg-cycle
/// row.
/// </summary>
public sealed record PeggedRepegPendingRaw(
    string FirmId,
    ulong AlgoId,
    ulong CancelledChildClOrdId,
    decimal TargetPrice,
    DateTimeOffset AtUtc);

/// <summary>
/// Pass-5 review (#296) P1. Raw per-Pegged cancelled-child history
/// row. <see cref="ChildClOrdIds"/> is FIFO oldest→newest so the
/// FIFO cap-eviction order survives a snapshot round-trip.
/// </summary>
public sealed record PeggedRepegHistoryRaw(
    string FirmId,
    ulong AlgoId,
    ulong[] ChildClOrdIds,
    bool EvictionLogged = false);

/// <summary>
/// Pass-1 review (#295) P1#1. Raw POV scheduling-progress row.
/// </summary>
public sealed record PovProgressRaw(
    string FirmId,
    ulong AlgoId,
    long MarketVolumeSeen,
    DateTimeOffset LastEvaluateAtUtc);

/// <summary>
/// Captured Order. Mutable scalars (<see cref="Status"/>,
/// <see cref="Leaves"/>, <see cref="Cum"/>, <see cref="IsStale"/>,
/// <see cref="StaleReason"/>, <see cref="StaledAtUtc"/>) are snapshotted
/// at lock-held capture time so the projection step does not re-read the
/// live <see cref="Order"/> aggregate — see RFC §4.3.
/// </summary>
public readonly record struct OrderRaw(
    Order Order,
    OrderStatus Status,
    long Leaves,
    long Cum,
    bool IsStale,
    string? StaleReason,
    DateTimeOffset? StaledAtUtc);

/// <summary>
/// Captured Algo. Same shape and §4.3 rationale as <see cref="OrderRaw"/>:
/// the engine mutates <see cref="Filled"/> / <see cref="Status"/> /
/// <see cref="Reason"/> / <see cref="TerminalAtUtc"/> in place, so we
/// capture them under the lock; immutable construction fields are read
/// off the <see cref="Algo"/> reference during projection.
/// </summary>
public readonly record struct AlgoRaw(
    Algo Algo,
    long Filled,
    AlgoStatus Status,
    AlgoTerminalReason Reason,
    DateTimeOffset? TerminalAtUtc);

public readonly record struct PositionRaw(
    string EndClientId,
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice);

public readonly record struct OwnershipRaw(ulong ClOrdId, string EndClientId);

public readonly record struct SessionPhaseOverrideRaw(string Symbol, SessionPhase Phase);

public readonly record struct CashRaw(string EndClientId, decimal Available);

/// <summary>
/// Q2.2 (#269). Raw lock-side capture of one row from
/// <see cref="B3.Trading.Application.CashKeeper"/>. Distinct from
/// <see cref="CashRaw"/> (which captures the fill-derived
/// <see cref="B3.Trading.Application.CashLedger"/>) so the two
/// projections can evolve independently.
/// </summary>
public readonly record struct CashKeeperRaw(string EndClientId, decimal Available);

/// <summary>
/// Q2.3 (#270). Raw lock-side capture of one row from
/// <see cref="B3.Trading.Application.FeeKeeper"/>: the running fee
/// total for one (end-client, day) bucket.
/// </summary>
public readonly record struct FeeKeeperRaw(string EndClientId, DateOnly Day, decimal Total);

/// <summary>
/// Q2.4 (#271). Raw lock-side capture of one (end-client, symbol, day)
/// realized-P&amp;L bucket from <see cref="B3.Trading.Application.PnlKeeper"/>.
/// </summary>
public readonly record struct PnlRealizedRaw(string EndClientId, string Symbol, DateOnly Day, decimal Realized);

/// <summary>
/// Q2.4 (#271). Raw lock-side capture of one (end-client, symbol)
/// avg-cost basis row tracked by <see cref="B3.Trading.Application.PnlKeeper"/>
/// in parallel with <see cref="B3.Trading.Application.PositionKeeper"/>.
/// </summary>
public readonly record struct PnlAvgCostRaw(string EndClientId, string Symbol, long NetQuantity, decimal AvgPrice);

/// <summary>
/// Pass-3 review (#278) P1. Raw lock-side capture of one
/// (end-client, symbol) "unknown basis" qty row tracked by
/// <see cref="B3.Trading.Application.PnlKeeper"/> after a legacy
/// snapshot rehydration. Persisting this set keeps a snapshot+tail
/// recovery from re-creating the phantom-P&amp;L bug on every restart.
/// </summary>
public readonly record struct PnlUnknownBasisRaw(string EndClientId, string Symbol, long NetQuantity);

public readonly record struct ClOrdIdCounterRaw(string EndClientId, ulong PrefixIdx, long Counter);

public readonly record struct AlgoIdCounterRaw(string FirmId, long Counter);

public readonly record struct BotOrderMappingRaw(
    ulong InternalClOrdId,
    Guid CredentialId,
    ulong ExternalClOrdId);

public readonly record struct BotCancelMappingRaw(
    ulong CancelInternalClOrdId,
    ulong OriginalInternalClOrdId,
    Guid CredentialId,
    ulong ExternalCancelClOrdId);

public readonly record struct ClOrdIdRegistryRaw(long NextPrefix, ClOrdIdCounterRaw[] Counters)
{
    public static ClOrdIdRegistryRaw Empty { get; } =
        new(0L, Array.Empty<ClOrdIdCounterRaw>());
}

/// <summary>
/// Pass-6 review (#299) P1. Raw lock-side capture of one
/// <see cref="B3.Trading.Application.PendingReplacementRegistry"/>
/// entry. Mirrors <see cref="B3.Trading.Application.PendingReplacementEntrySnapshot"/>
/// 1:1 — the two layers are deliberately distinct so the raw-capture
/// shape can evolve independently of the persisted DTO if needed.
/// </summary>
public readonly record struct PendingReplacementRaw(
    ulong OriginalClOrdId,
    ulong NewClOrdId,
    string OwnerEndClientId,
    string Symbol,
    ulong SecurityId,
    OrderSide Side,
    OrderType Type,
    long NewQuantity,
    decimal? NewPrice,
    string FirmId,
    ulong? ParentAlgoId,
    int? AlgoSliceSeq,
    TimeInForce? RequestedTimeInForce,
    decimal? RequestedStopPrice,
    DateTimeOffset? RequestedGoodTillDate,
    DateTimeOffset CreatedAtUtc,
    bool AmbiguousMarginHeld,
    DateTimeOffset? AmbiguousAtUtc,
    decimal NewRemainingNotional);
