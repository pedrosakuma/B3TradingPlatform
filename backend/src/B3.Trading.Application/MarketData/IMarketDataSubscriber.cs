namespace B3.Trading.Application.MarketData;

/// <summary>
/// Application-side seam over the market-data SDK so risk logic can be
/// unit-tested without touching the network. The host wires
/// <c>SdkMarketDataSubscriber</c> (wraps <c>MarketDataClient</c>); tests
/// wire a fake that synchronously raises events on demand.
///
/// <para>
/// Implementations MUST raise <see cref="Trade"/> / <see cref="InfoSnapshot"/>
/// with already-validated payloads (positive prices, non-empty symbol).
/// Consumers can still defensively skip junk, but the seam exists to
/// keep that policy out of risk-side code paths.
/// </para>
/// </summary>
public interface IMarketDataSubscriber : IAsyncDisposable
{
    MarketDataConnectionState State { get; }

    /// <summary>Last-observed dropped-event count from the underlying
    /// bounded back-pressure channel. Surfaced as an observable gauge
    /// for ops alerting.</summary>
    long DroppedEventCount { get; }

    event Action<MarketTrade>? Trade;
    event Action<MarketInfoSnapshot>? InfoSnapshot;
    event Action<MarketDataConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised when the upstream rejects a Subscribe (unknown symbol,
    /// not ready, …). Symbol-mapping mismatches between this host and
    /// the MD platform surface here.
    /// </summary>
    event Action<MarketSubscribeError>? SubscribeError;

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Subscribes to trade prints + info snapshots for <paramref name="symbol"/>.
    /// Idempotent on the SDK side.
    /// </summary>
    ValueTask SubscribeAsync(string symbol, CancellationToken ct = default);
}

public readonly record struct MarketSubscribeError(string Symbol, string Reason);
