namespace B3.Trading.Application.Persistence;

/// <summary>
/// Plain DTOs used to serialise the in-memory state of the platform to a
/// snapshot file. All collections are materialised lists (snapshot is a
/// point-in-time photograph) rather than lazy enumerables, so the captured
/// state is independent of subsequent mutations.
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
    /// User-issued bot credentials (sub-issue #169). Empty on snapshots
    /// pre-dating the field — credentials are reconstructed instead by
    /// the WAL replay path on top of the empty list, which matches the
    /// "no PATs minted" semantics those snapshots actually carried.
    /// </summary>
    public List<UserBotCredentialSnapshot> UserBotCredentials { get; init; } = new();
}

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
    decimal? TwapChildPrice = null);

public sealed record PositionSnapshot(
    string EndClientId,
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice);

public sealed record CashBalanceSnapshot(
    string EndClientId,
    decimal Available);

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
