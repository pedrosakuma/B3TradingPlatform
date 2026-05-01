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
    public ClOrdIdRegistrySnapshot ClOrdIds { get; init; } = new();
    public List<OwnershipMappingSnapshot> Ownership { get; init; } = new();
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
    string FirmId = "DEFAULT");

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
