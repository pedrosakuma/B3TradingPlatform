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
}

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
