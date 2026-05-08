namespace B3.Trading.Infrastructure;

/// <summary>
/// Read-only snapshot of how the exchange wire side was wired, registered
/// as a singleton at host startup so <c>/health</c> can surface
/// <c>exchange.{mode, readyForOrders, firmCount}</c> without poking DI.
/// <para>
/// <c>ReadyForOrders</c> here reflects configuration only — a
/// <see cref="ExchangeMode.Real"/> gateway with a disconnected FIXP
/// session is still <c>true</c> at this layer. The lifecycle endpoint
/// AND-s this against per-firm live session state from
/// <see cref="IFirmSessionStatusProvider"/> when one is registered, so the
/// JSON-facing <c>readyForOrders</c> only flips green when every
/// configured firm is actually <c>established</c>.
/// </para>
/// </summary>
public sealed class ExchangeStatus
{
    public ExchangeStatus(ExchangeMode mode, int firmCount, bool erInjectionEnabled = false)
    {
        Mode = mode;
        FirmCount = firmCount;
        ErInjectionEnabled = erInjectionEnabled;
    }

    public ExchangeMode Mode { get; }
    public int FirmCount { get; }

    /// <summary>
    /// True when the host booted with <c>Trading:Exchange:AllowErInjection=true</c>
    /// and the in-process Mock gateway is active. Surfaced on <c>/health</c>
    /// so the demo-driver and dashboards can detect synthetic-ER capability
    /// without coupling to the legacy <c>Mode==Simulator</c> string check
    /// (#163).
    /// </summary>
    public bool ErInjectionEnabled { get; }

    public static ExchangeStatus FromOptions(ExchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mode = options.ResolveMode();
        var count = mode == ExchangeMode.Unavailable
            ? 0
            : options.Firms.Count(f => !string.IsNullOrWhiteSpace(f.FirmId));
        // Validator already guarantees AllowErInjection=true ⇒ Mode=Mock,
        // but we re-check the mode here so a misconfigured composition
        // root that bypasses validation still surfaces the safe value.
        var erInjection = options.AllowErInjection && mode == ExchangeMode.Mock;
        return new ExchangeStatus(mode, count, erInjection);
    }

    /// <summary>
    /// True when submits will reach a gateway implementation that does NOT
    /// fail-closed. <see cref="ExchangeMode.Unavailable"/> is the only mode
    /// that returns false; <see cref="ExchangeMode.Stub"/> still returns
    /// true because it accepts (silently) — that's its whole point.
    /// </summary>
    public bool ReadyForOrders => Mode != ExchangeMode.Unavailable;
}
