using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3). The fully-resolved
/// absolute payload for one <c>POST /api/admin/accounts/{endClientId}/reset</c>
/// request — computed ONCE, at request time, from the live
/// <see cref="PositionKeeper"/> state plus the then-current
/// <see cref="CashSeedOptions"/> / <see cref="PositionSeedOptions"/>
/// configuration, and persisted verbatim inside the resulting
/// <see cref="AccountResetEvent"/>. Replay reads this payload back and
/// applies it byte-for-byte — it NEVER re-resolves seed configuration
/// (see <c>EventReplayer.Apply</c>'s <see cref="AccountResetEvent"/>
/// case), so recovery stays deterministic even if an operator edits
/// <c>Trading:Cash:Seeds</c> / <c>Trading:Positions:Seeds</c> between
/// the live reset and a later cold/snapshot+tail replay.
/// </summary>
public sealed record AccountResetPayload(
    decimal CashAvailable,
    IReadOnlyList<AccountResetPositionEntry> Positions);

/// <summary>
/// Pure, side-effect-free resolver for <see cref="AccountResetPayload"/>.
/// Deliberately takes the caller's already-fetched position snapshot
/// (<see cref="PositionKeeper.ForEndClientAndFirm"/>) rather than a
/// <see cref="PositionKeeper"/> reference, so the same scan the caller
/// needs for "before" (rollback) capture is not repeated here, and so
/// this type stays trivially unit-testable without standing up a
/// keeper.
/// </summary>
public static class AccountResetPayloadResolver
{
    /// <summary>
    /// Resolves the absolute reset target.
    ///
    /// <para>
    /// Cash: the matching <see cref="CashSeed"/> row for
    /// (<paramref name="firmId"/>, <paramref name="endClientId"/>)
    /// — firm compared <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// (mirrors <see cref="CashLedger"/>/<see cref="CashKeeper"/>'s own
    /// firm-key comparer), end-client compared
    /// <see cref="StringComparison.Ordinal"/> (mirrors
    /// <see cref="EndClientId"/>'s default record equality) — else
    /// <c>0m</c> when unconfigured. <c>CashSeedOptions.SignupInitialBalance</c>
    /// is a firm-agnostic global signup default, not a per-end-client
    /// seed, and is intentionally NOT consulted here.
    /// </para>
    ///
    /// <para>
    /// Positions: the union of (a) every symbol in
    /// <paramref name="currentPositions"/> that is currently non-flat
    /// (<c>NetQuantity != 0</c>) — flattened to <c>(0, 0m)</c> unless
    /// overridden by (b) — and (b) every <see cref="PositionSeed"/> row
    /// configured for (<paramref name="firmId"/>,
    /// <paramref name="endClientId"/>) (firm normalised via
    /// <see cref="PositionKeeper.NormalizeFirmId"/>, matching the
    /// startup seeder's own comparison; end-client compared
    /// <see cref="StringComparison.Ordinal"/>) — seeded to its
    /// configured (Quantity, AverageEntryPrice). A seed row always wins
    /// over the flatten-to-zero default for the same symbol. The
    /// result is sorted by symbol (ordinal) for deterministic WAL
    /// payload ordering.
    /// </para>
    /// </summary>
    public static AccountResetPayload Resolve(
        string firmId,
        EndClientId endClientId,
        IReadOnlyCollection<Position> currentPositions,
        CashSeedOptions cashSeeds,
        PositionSeedOptions positionSeeds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentNullException.ThrowIfNull(currentPositions);
        ArgumentNullException.ThrowIfNull(cashSeeds);
        ArgumentNullException.ThrowIfNull(positionSeeds);

        var cashAvailable = 0m;
        foreach (var seed in cashSeeds.Seeds)
        {
            if (string.Equals(seed.FirmId, firmId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(seed.EndClientId, endClientId.Value, StringComparison.Ordinal))
            {
                cashAvailable = seed.InitialAvailable;
                break;
            }
        }

        var normalizedFirm = PositionKeeper.NormalizeFirmId(firmId);
        var targets = new Dictionary<string, (long Quantity, decimal AveragePrice)>(StringComparer.Ordinal);
        foreach (var position in currentPositions)
        {
            if (position.NetQuantity == 0) continue;
            targets[position.Symbol] = (0L, 0m);
        }
        foreach (var seed in positionSeeds.Seeds)
        {
            var seedFirm = PositionKeeper.NormalizeFirmId(seed.Firm ?? PositionKeeper.DefaultFirmId);
            if (!string.Equals(seedFirm, normalizedFirm, StringComparison.Ordinal)) continue;
            if (!string.Equals(seed.EndClientId, endClientId.Value, StringComparison.Ordinal)) continue;
            targets[seed.Symbol] = (seed.Quantity, seed.AverageEntryPrice);
        }

        var positionEntries = targets.Count == 0
            ? Array.Empty<AccountResetPositionEntry>()
            : targets
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => new AccountResetPositionEntry(kv.Key, kv.Value.Quantity, kv.Value.AveragePrice))
                .ToArray();

        return new AccountResetPayload(cashAvailable, positionEntries);
    }
}
