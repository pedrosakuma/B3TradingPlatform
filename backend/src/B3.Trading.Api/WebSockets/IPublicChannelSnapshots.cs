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

/// <summary>
/// Q3.6 Stage B (#286). Composite snapshot provider that delegates to
/// each inner provider in order, returning the first non-null result.
/// Used to multiplex independent sinks (auction, book, …) behind the
/// single <see cref="IPublicChannelSnapshots"/> the WS hub depends on
/// — each sink owns one or more <see cref="PublicChannelKind"/> values
/// and returns <c>null</c> for kinds it does not handle.
/// </summary>
public sealed class CompositePublicChannelSnapshots : IPublicChannelSnapshots
{
    private readonly IPublicChannelSnapshots[] _inner;

    public CompositePublicChannelSnapshots(params IPublicChannelSnapshots[] inner)
    {
        _inner = inner ?? Array.Empty<IPublicChannelSnapshots>();
    }

    public object? GetSnapshot(PublicChannelKind kind, string symbol)
    {
        foreach (var provider in _inner)
        {
            var snap = provider.GetSnapshot(kind, symbol);
            if (snap is not null) return snap;
        }
        return null;
    }
}
