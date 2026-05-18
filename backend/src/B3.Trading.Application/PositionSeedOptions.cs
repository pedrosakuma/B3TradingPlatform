namespace B3.Trading.Application;

/// <summary>
/// Optional starting positions applied to <see cref="PositionKeeper"/>
/// at process startup. Intended for dogfood / dev environments where
/// the naked-short gate would otherwise block any first Sell — seeding
/// an end-client with N units of a symbol lets them sell up to that
/// quantity before having to buy.
///
/// <para>
/// Seeds are applied <b>only when the position is absent</b> (after
/// snapshot/WAL recovery), so a warm restart preserves the actual
/// trading state and never overwrites real fills with the seed.
/// </para>
/// </summary>
public sealed class PositionSeedOptions
{
    public const string SectionName = "Trading:Positions";

    /// <summary>
    /// Per-end-client / per-symbol opening positions. The list shape
    /// (rather than a nested dict) keeps env-var binding ergonomic:
    /// <c>Trading__Positions__Seeds__0__EndClientId=alice</c>.
    /// </summary>
    public List<PositionSeed> Seeds { get; set; } = new();
}

public sealed class PositionSeed
{
    public string EndClientId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// PR #316 P2. Optional firm bucket. When unset (legacy config) the
    /// seed lands in <see cref="PositionKeeper.DefaultFirmId"/>, which
    /// is invisible to real-mode users authenticated under FIRM01 / FIRM02
    /// — every multi-firm deployment must populate this explicitly. The
    /// startup seeder logs a one-shot warning if any seed lacks
    /// <see cref="Firm"/> while more than one firm is configured under
    /// <c>Trading:Auth:Users</c>.
    /// </summary>
    public string? Firm { get; set; }

    /// <summary>
    /// Net quantity. Positive = long (the only useful seed today, since
    /// the naked-short gate cares about the long side); negative values
    /// are accepted by <see cref="PositionKeeper"/> but no current risk
    /// check rewards seeding a short.
    /// </summary>
    public long Quantity { get; set; }

    /// <summary>
    /// Average entry price stamped on the seeded position. Reported on
    /// <c>positions.me</c> and used by P&amp;L diagnostics; zero is fine
    /// when the dogfood scenario doesn't care about avg-cost accuracy.
    /// </summary>
    public decimal AverageEntryPrice { get; set; }
}
