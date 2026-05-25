using B3.Trading.Domain;

namespace B3.Trading.Application.SubAccount;

/// <summary>
/// Default <see cref="IVenueAccountResolver"/>: always returns
/// <c>null</c>. Pre-#458 wire behavior — orders carry no CBLC
/// account number; post-trade allocation continues to rely on the
/// broker's out-of-band matching. Production operators replace this
/// with a real resolver (lookup table, admin-managed registry,
/// broker handshake) at the composition root the day they need the
/// wire field populated.
/// </summary>
public sealed class NullVenueAccountResolver : IVenueAccountResolver
{
    public static readonly NullVenueAccountResolver Instance = new();

    public ulong? TryResolve(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return null;
    }
}
