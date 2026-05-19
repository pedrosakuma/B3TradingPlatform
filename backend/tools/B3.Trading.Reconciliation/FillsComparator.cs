using System.Globalization;

namespace B3.Trading.Reconciliation;

/// <summary>
/// Pure comparator that diffs the matching platform's fills CSV
/// against the trading-host's statement fills for a single firm on a
/// single UTC date. The exporter on the matching side emits one row
/// per trade with both firms; we project the firm-relative view by
/// expanding internal crosses (buyFirm == sellFirm == ourFirm) into
/// two host-side fills (a Buy and a Sell), matching the trading-host
/// statement shape (FillRowDto per ER, not per trade).
///
/// <para>
/// The comparator is intentionally aggregate-first: ExecutionId on the
/// trading-host side is synthesised as <c>{clOrdId}:{cumQty}</c> and
/// is therefore not a join key against the matching <c>tradeId</c>.
/// We compare <c>(symbol, side)</c> aggregates (count, sum of qty, sum
/// of notional = qty * price); any divergence produces a detailed
/// per-bucket report identifying which (symbol, side) bucket drifted.
/// </para>
/// </summary>
public static class FillsComparator
{
    public static ReconciliationReport Compare(
        IReadOnlyList<MatchingFillRow> matching,
        IReadOnlyList<HostFillRow> host,
        string firmId)
    {
        ArgumentNullException.ThrowIfNull(matching);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(firmId);

        var matchingProjected = ProjectMatchingForFirm(matching, firmId).ToList();
        var matchingBuckets = Aggregate(matchingProjected.Select(p =>
            (p.Symbol, p.Side, p.Quantity, p.Price)));
        var hostBuckets = Aggregate(host.Select(h =>
            (h.Symbol, NormaliseSide(h.Side), h.Quantity, h.Price)));

        var diffs = new List<BucketDiff>();
        var allKeys = matchingBuckets.Keys.Union(hostBuckets.Keys).OrderBy(k => k.Symbol).ThenBy(k => k.Side);
        foreach (var key in allKeys)
        {
            var m = matchingBuckets.TryGetValue(key, out var mv) ? mv : Aggregates.Zero;
            var h = hostBuckets.TryGetValue(key, out var hv) ? hv : Aggregates.Zero;
            if (m != h)
            {
                diffs.Add(new BucketDiff(key.Symbol, key.Side, m, h));
            }
        }

        return new ReconciliationReport(
            FirmId: firmId,
            MatchingRowCount: matchingProjected.Count,
            HostRowCount: host.Count,
            Diffs: diffs);
    }

    /// <summary>
    /// Yields one entry per (firm-relative side, ClOrdId) — internal
    /// crosses (same firm on both sides) emit two entries; outbound
    /// matches emit one with the firm-relative side derived from
    /// buyFirm / sellFirm.
    /// </summary>
    public static IEnumerable<HostShapedRow> ProjectMatchingForFirm(
        IEnumerable<MatchingFillRow> matching, string firmId)
    {
        foreach (var row in matching)
        {
            var isBuy = string.Equals(row.BuyFirm, firmId, StringComparison.Ordinal);
            var isSell = string.Equals(row.SellFirm, firmId, StringComparison.Ordinal);
            if (!isBuy && !isSell) continue;
            if (isBuy)
            {
                yield return new HostShapedRow(
                    Symbol: row.Symbol, Side: "Buy",
                    Quantity: row.Quantity, Price: row.Price,
                    ClOrdId: row.BuyClOrdId, TimestampUtc: row.TimestampUtc,
                    TradeId: row.TradeId);
            }
            if (isSell)
            {
                yield return new HostShapedRow(
                    Symbol: row.Symbol, Side: "Sell",
                    Quantity: row.Quantity, Price: row.Price,
                    ClOrdId: row.SellClOrdId, TimestampUtc: row.TimestampUtc,
                    TradeId: row.TradeId);
            }
        }
    }

    private static Dictionary<(string Symbol, string Side), Aggregates> Aggregate(
        IEnumerable<(string Symbol, string Side, long Quantity, decimal Price)> rows)
    {
        var map = new Dictionary<(string, string), Aggregates>();
        foreach (var r in rows)
        {
            var key = (r.Symbol, r.Side);
            map.TryGetValue(key, out var prev);
            map[key] = new Aggregates(
                Count: prev.Count + 1,
                TotalQty: prev.TotalQty + r.Quantity,
                TotalNotional: prev.TotalNotional + r.Quantity * r.Price);
        }
        return map;
    }

    /// <summary>
    /// Normalises trading-host side strings ("Buy" / "Sell" or
    /// "BUY" / "SELL" depending on serialisation history) to the
    /// canonical "Buy" / "Sell" used in the comparator.
    /// </summary>
    public static string NormaliseSide(string side) =>
        side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? "Buy" :
        side.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? "Sell" :
        side;
}

public readonly record struct Aggregates(int Count, long TotalQty, decimal TotalNotional)
{
    public static readonly Aggregates Zero = new(0, 0, 0m);

    public string Pretty() =>
        $"count={Count}, qty={TotalQty.ToString(CultureInfo.InvariantCulture)}, " +
        $"notional={TotalNotional.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record BucketDiff(string Symbol, string Side, Aggregates Matching, Aggregates Host);

public sealed record HostShapedRow(
    string Symbol,
    string Side,
    long Quantity,
    decimal Price,
    string ClOrdId,
    DateTimeOffset TimestampUtc,
    string TradeId);

public sealed record ReconciliationReport(
    string FirmId,
    int MatchingRowCount,
    int HostRowCount,
    IReadOnlyList<BucketDiff> Diffs)
{
    public bool IsClean => Diffs.Count == 0;

    public string Render()
    {
        if (IsClean)
            return $"firm={FirmId}: reconciliation clean — {MatchingRowCount} matching row(s) match {HostRowCount} host row(s) across all (symbol, side) buckets.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"firm={FirmId}: reconciliation DIFFERS — {Diffs.Count} bucket(s) drifted.");
        sb.AppendLine($"  totals: matching={MatchingRowCount} host={HostRowCount}");
        foreach (var d in Diffs)
        {
            sb.AppendLine($"  - {d.Symbol}/{d.Side}: matching[{d.Matching.Pretty()}] vs host[{d.Host.Pretty()}]");
        }
        return sb.ToString();
    }
}
