namespace B3.Trading.Infrastructure;

/// <summary>
/// Read-only snapshot of how the exchange wire side was wired, registered
/// as a singleton at host startup so <c>/health</c> can surface
/// <c>exchange.{mode, readyForOrders, firmCount}</c> without poking DI.
/// <para>
/// <c>ReadyForOrders</c> reflects configuration, not live session state —
/// a <see cref="ExchangeMode.Real"/> gateway with a disconnected FIXP
/// session is still <c>true</c> here. Live connection state surfaces
/// separately via <c>trading.entrypoint.connected</c> metrics and (Phase 8)
/// per-firm readiness.
/// </para>
/// </summary>
public sealed class ExchangeStatus
{
    public ExchangeStatus(ExchangeMode mode, int firmCount)
    {
        Mode = mode;
        FirmCount = firmCount;
    }

    public ExchangeMode Mode { get; }
    public int FirmCount { get; }

    /// <summary>
    /// True when submits will reach a gateway implementation that does NOT
    /// fail-closed. <see cref="ExchangeMode.Unavailable"/> is the only mode
    /// that returns false; <see cref="ExchangeMode.Stub"/> still returns
    /// true because it accepts (silently) — that's its whole point.
    /// </summary>
    public bool ReadyForOrders => Mode != ExchangeMode.Unavailable;
}
