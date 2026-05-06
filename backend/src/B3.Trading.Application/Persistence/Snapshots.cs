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
    public ClOrdIdRegistrySnapshot ClOrdIds { get; init; } = new();
    public List<OwnershipMappingSnapshot> Ownership { get; init; } = new();
    public List<AlgoSnapshot> Algos { get; init; } = new();
    public AlgoIdRegistrySnapshot AlgoIds { get; init; } = new();
}

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
    int? AlgoSliceSeq = null);

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
