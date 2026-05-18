namespace B3.Trading.Application.MarketData;

/// <summary>
/// Q4.7 (#307). Point-in-time snapshot of the top-of-book at the instant
/// an exchange Fill / PartialFill is observed by the trading host. The
/// snapshot is captured from the existing <see cref="PegBookTopCache"/>
/// (Q3.3) — the only in-process cache of best bid / best ask / last trade
/// — and attached to the resulting <see cref="ExecutionEvent"/> + WAL
/// <see cref="Persistence.ExecutionReportReceivedEvent"/> for compliance
/// / best-execution evidence.
///
/// <para>
/// <b>Additive on the wire.</b> The field is optional on every carrier
/// (ExecutionEvent, WAL ER event, ExecutionDto) so older binaries +
/// older WAL segments round-trip cleanly — a missing payload
/// deserialises as <c>null</c> (no touch was captured, e.g. cancels /
/// rejects, or pre-#307 fills).
/// </para>
///
/// <para>
/// <b>Staleness rule.</b> If <see cref="PegBookTopCache"/> has no entry
/// for the fill's symbol, the snapshot is captured with all price legs
/// <c>null</c> and <see cref="Stale"/> = <c>true</c>. If the cache has
/// an entry but its <see cref="BookTop.UpdatedUtc"/> is more than
/// <see cref="DefaultFreshnessWindow"/> behind the fill timestamp, the
/// snapshot still carries the (last-known) prices but <see cref="Stale"/>
/// is set so compliance can distinguish "no live reference at fill
/// time" from "fresh book-top".
/// </para>
/// </summary>
public sealed record BookTouchSnapshot
{
    /// <summary>
    /// Q4.7 (#307). Acceptance window for "fresh enough" book-top data.
    /// Any cache entry older than this relative to the fill timestamp is
    /// flagged <see cref="Stale"/> per the issue spec (500ms).
    /// </summary>
    public static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromMilliseconds(500);

    public decimal? BestBid { get; init; }
    public decimal? BestAsk { get; init; }
    public decimal? MidPrice { get; init; }
    public decimal? LastTradePrice { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public bool Stale { get; init; }

    /// <summary>
    /// Captures the current top-of-book for <paramref name="symbol"/> from
    /// <paramref name="cache"/> against the fill timestamp
    /// <paramref name="fillTimestampUtc"/>. The freshness window defaults
    /// to <see cref="DefaultFreshnessWindow"/>; tests can override it.
    /// Always returns a non-null snapshot — when the cache is empty the
    /// price legs are <c>null</c> and <see cref="Stale"/> is <c>true</c>.
    /// </summary>
    public static BookTouchSnapshot Capture(
        PegBookTopCache? cache,
        string symbol,
        DateTimeOffset fillTimestampUtc,
        TimeSpan? freshnessWindow = null)
    {
        var window = freshnessWindow ?? DefaultFreshnessWindow;
        var top = cache?.TryGet(symbol);
        if (top is null)
        {
            return new BookTouchSnapshot
            {
                BestBid = null,
                BestAsk = null,
                MidPrice = null,
                LastTradePrice = null,
                CapturedAtUtc = fillTimestampUtc,
                Stale = true,
            };
        }

        var t = top.Value;
        var age = fillTimestampUtc - t.UpdatedUtc;
        var stale = age > window;
        return new BookTouchSnapshot
        {
            BestBid = t.BestBid,
            BestAsk = t.BestAsk,
            MidPrice = t.Mid,
            LastTradePrice = t.Last,
            CapturedAtUtc = fillTimestampUtc,
            Stale = stale,
        };
    }
}
