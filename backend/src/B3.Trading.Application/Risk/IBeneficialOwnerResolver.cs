using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk;

/// <summary>
/// #433. Maps an <see cref="EndClientId"/> (platform-level "owner") to
/// its <em>beneficial owner</em> — the real-world legal person / entity
/// whose interest the order represents. A single beneficial owner can
/// hold multiple platform owners (e.g. one CPF traded via two different
/// firms registered on this trading host), and self-trade prevention
/// (CVM 168 práticas equitativas) must cover wash-trades across that
/// fan-out.
///
/// <para>
/// Default policy when no mapping is configured: beneficial owner ==
/// owner id. This is back-compat with every existing deployment — the
/// cross-firm STP scope is opt-in via
/// <see cref="RiskLimits.EnforceCrossFirmStp"/> AND requires explicit
/// configuration of overlapping owner→BO entries to take effect.
/// </para>
/// </summary>
public interface IBeneficialOwnerResolver
{
    /// <summary>Resolves the beneficial-owner id of a platform owner.</summary>
    string Resolve(EndClientId owner);

    /// <summary>
    /// Returns every platform owner registered for the given beneficial
    /// owner — including the queried owner itself when its mapping (or
    /// implicit self-mapping) hits the same beneficial owner. The
    /// returned list always contains at least the input owner.
    /// </summary>
    IReadOnlyCollection<EndClientId> OwnersFor(string beneficialOwnerId);
}

/// <summary>
/// Options-backed implementation. Bound from
/// <c>Trading:Risk:BeneficialOwners</c>: a flat
/// <c>owner_id → beneficial_owner_id</c> dictionary. Lookups for owners
/// not in the map collapse to <c>owner_id == beneficial_owner_id</c>,
/// preserving the pre-#433 semantics.
/// </summary>
public sealed class OptionsBeneficialOwnerResolver : IBeneficialOwnerResolver
{
    private readonly IOptionsMonitor<RiskOptions> _options;

    public OptionsBeneficialOwnerResolver(IOptionsMonitor<RiskOptions> options)
    {
        _options = options;
    }

    public string Resolve(EndClientId owner)
    {
        var map = _options.CurrentValue.BeneficialOwners;
        return map.TryGetValue(owner.Value, out var bo) && !string.IsNullOrWhiteSpace(bo)
            ? bo
            : owner.Value;
    }

    public IReadOnlyCollection<EndClientId> OwnersFor(string beneficialOwnerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beneficialOwnerId);
        var map = _options.CurrentValue.BeneficialOwners;
        var owners = new List<EndClientId>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in map)
        {
            if (string.Equals(kv.Value, beneficialOwnerId, StringComparison.OrdinalIgnoreCase)
                && seen.Add(kv.Key))
            {
                owners.Add(new EndClientId(kv.Key));
            }
        }

        // Include the implicit self-mapping fallback whenever the BO id
        // itself does not explicitly resolve somewhere else. This keeps
        // mixed explicit/implicit sibling configurations complete
        // without overriding an explicit owner -> different BO mapping.
        if ((!map.TryGetValue(beneficialOwnerId, out var explicitBo)
             || string.IsNullOrWhiteSpace(explicitBo)
             || string.Equals(explicitBo, beneficialOwnerId, StringComparison.OrdinalIgnoreCase))
            && seen.Add(beneficialOwnerId))
        {
            owners.Add(new EndClientId(beneficialOwnerId));
        }

        return owners;
    }
}
