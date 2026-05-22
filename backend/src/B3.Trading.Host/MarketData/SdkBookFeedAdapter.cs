using B3.MarketData.WebSocketClient;
using B3.Trading.Application.MarketData;
using AppL2Ladder = B3.Trading.Application.MarketData.L2Ladder;
using AppL2Side = B3.Trading.Application.MarketData.L2Side;
using AppL2TopOfBook = B3.Trading.Application.MarketData.L2TopOfBook;
using SdkL2Level = B3.MarketData.WebSocketClient.L2Level;

namespace B3.Trading.Host.MarketData;

/// <summary>
/// Adapter from the SDK 0.4.0 <see cref="IBookFeed"/> (B3MarketDataPlatform #43 /
/// #44 / #53) to the application-side <see cref="IL2BookView"/> seam consumed by
/// <c>MboPegBookPump</c>.
///
/// <para>
/// The SDK's <c>BookFeed</c> already maintains a per-symbol L3 (MBO) book and
/// derives an aggregate L2 top — including server-driven stale gating via
/// <see cref="IBookView.IsStale"/> and auto-evict on
/// <c>MarketDataClient.UnsubscribeAsync</c> (Phase 2). This adapter is a thin
/// translator: it forwards the SDK's <c>Changed</c> event as
/// <see cref="IL2BookView.BookChanged"/> and converts SDK depth queries
/// (<see cref="IBookView.CopyBidLevels"/> / <see cref="IBookView.CopyAskLevels"/>
/// span-based, <c>DateTime</c> UTC timestamps) into the application-owned
/// <see cref="L2TopOfBook"/> / <see cref="L2Ladder"/> shapes
/// (<c>DateTimeOffset</c>, <see cref="IReadOnlyList{T}"/>).
/// </para>
///
/// <para>
/// Stale gating: the SDK exposes <see cref="IBookView.IsStale"/> per symbol but
/// the application-side <see cref="IL2BookView"/> surface does not (yet) carry
/// a stale axis — consumers gate off staleness through their own paths
/// (<c>SymbolHaltService</c>, <c>PegBookTopCache</c> freshness). To avoid
/// publishing dead data, this adapter SUPPRESSES <see cref="GetTopOfBook"/> /
/// <see cref="GetLadder"/> when the SDK reports the book as stale; the BookChanged
/// notification still fires so consumers can recompute their own snapshot.
/// </para>
/// </summary>
internal sealed class SdkBookFeedAdapter : IL2BookView
{
    private readonly IBookFeed _feed;

    public event Action<string>? BookChanged;

    public SdkBookFeedAdapter(IBookFeed feed)
    {
        _feed = feed;
        _feed.Changed += OnChanged;
    }

    private void OnChanged(string symbol) => BookChanged?.Invoke(symbol);

    public AppL2TopOfBook? GetTopOfBook(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var view = _feed.GetBook(symbol);
        if (view is null || view.IsStale) return null;
        if (!view.TryGetTop(out var top)) return null;
        return new AppL2TopOfBook(
            top.Symbol,
            new AppL2Side(top.Bid.Price, top.Bid.TotalQty, top.Bid.OrderCount),
            new AppL2Side(top.Ask.Price, top.Ask.TotalQty, top.Ask.OrderCount),
            new DateTimeOffset(DateTime.SpecifyKind(top.UpdatedUtc, DateTimeKind.Utc)));
    }

    public AppL2Ladder? GetLadder(string symbol, int maxLevels)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (maxLevels <= 0) throw new ArgumentOutOfRangeException(nameof(maxLevels));
        var view = _feed.GetBook(symbol);
        if (view is null || view.IsStale) return null;

        var pool = System.Buffers.ArrayPool<SdkL2Level>.Shared;
        var bidBuf = pool.Rent(maxLevels);
        var askBuf = pool.Rent(maxLevels);
        try
        {
            var bidCount = view.CopyBidLevels(bidBuf.AsSpan(0, maxLevels), maxLevels);
            var askCount = view.CopyAskLevels(askBuf.AsSpan(0, maxLevels), maxLevels);
            if (bidCount == 0 && askCount == 0) return null;

            return new AppL2Ladder(
                view.Symbol,
                CopyLevels(bidBuf.AsSpan(0, bidCount)),
                CopyLevels(askBuf.AsSpan(0, askCount)),
                new DateTimeOffset(DateTime.SpecifyKind(view.UpdatedUtc, DateTimeKind.Utc)));
        }
        finally
        {
            pool.Return(bidBuf);
            pool.Return(askBuf);
        }
    }

    private static IReadOnlyList<AppL2Side> CopyLevels(ReadOnlySpan<SdkL2Level> src)
    {
        if (src.Length == 0) return Array.Empty<AppL2Side>();
        var dst = new AppL2Side[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            dst[i] = new AppL2Side(src[i].Price, src[i].TotalQty, src[i].OrderCount);
        }
        return dst;
    }
}
