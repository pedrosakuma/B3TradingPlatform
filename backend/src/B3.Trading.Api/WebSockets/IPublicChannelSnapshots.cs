namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Resolves the snapshot frame payload to send when a client first
/// subscribes to a public per-symbol channel
/// (<see cref="Channels.PhasesPrefix"/> / <see cref="Channels.AuctionPrefix"/>).
///
/// <para>
/// The implementation lives next to the data source (e.g. the auction
/// state-store sink); this seam keeps the hub from taking a hard
/// dependency on every market-data store. Returning <c>null</c> is
/// legitimate — it ships an empty snapshot, which is the right
/// initial frame when nothing has been observed yet.
/// </para>
/// </summary>
public interface IPublicChannelSnapshots
{
    object? GetSnapshot(PublicChannelKind kind, string symbol);
}

/// <summary>
/// Default no-op implementation, used when no auction state-store is
/// wired (tests, dev loop with the public channels disabled). Returns
/// <c>null</c> for everything; subscribers still get an empty
/// <c>snapshot</c> frame followed by future deltas.
/// </summary>
public sealed class NullPublicChannelSnapshots : IPublicChannelSnapshots
{
    public object? GetSnapshot(PublicChannelKind kind, string symbol) => null;
}
