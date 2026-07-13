using B3.Trading.Domain;

namespace B3.Trading.Application.Investor;

/// <summary>
/// Default <see cref="IInvestorIdResolver"/>: always returns
/// <c>null</c>. Pre-#472 wire behavior — orders carry no
/// <c>InvestorId</c>; the broker handles any out-of-band regulatory
/// attribution. Production operators replace this with a real
/// resolver (broker registry lookup, CBLC association table) at the
/// composition root the day they need the wire field populated.
/// </summary>
public sealed class NullInvestorIdResolver : IInvestorIdResolver
{
    public static readonly NullInvestorIdResolver Instance = new();

    public InvestorIdentity? TryResolve(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return null;
    }
}
