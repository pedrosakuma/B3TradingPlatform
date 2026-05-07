namespace B3.Trading.Infrastructure;

/// <summary>
/// Per-firm live FIXP session snapshot. Surfaced via <c>/health</c> so the
/// frontend, dashboards, and ops can answer "is firm X actually able to send
/// orders right now?" without scraping metrics. Independent of
/// <see cref="ExchangeStatus"/>, which only reflects configuration.
/// </summary>
/// <param name="FirmId">Firm identifier (matches <c>FirmConfig.FirmId</c>).</param>
/// <param name="SessionState">Lower-snake-case tag of the live SDK
/// <c>FixpClientState</c> — e.g. <c>established</c>, <c>suspended</c>,
/// <c>terminated</c>. Mirrors the <c>trading.entrypoint.session_state</c>
/// metric tag.</param>
/// <param name="IsReconnecting">True when the gateway's auto-reconnect loop
/// is currently running for this firm.</param>
/// <param name="SessionVerId">Last <c>SessionVerId</c> the gateway has tried
/// to use. Bumps on every reconnect attempt.</param>
public sealed record FirmSessionStatus(
    string FirmId,
    string SessionState,
    bool IsReconnecting,
    uint SessionVerId)
{
    /// <summary>Tag value emitted by <see cref="FixpStateGaugeProjector"/>
    /// for the SDK's healthy steady state.</summary>
    public const string EstablishedState = "established";

    /// <summary>True when this firm is in the only state where new orders can
    /// reach the venue. Anything else (suspended, reconnecting, disconnected)
    /// means a submit will throw <c>InvalidOperationException</c> at the SDK
    /// boundary, regardless of <see cref="ExchangeStatus.ReadyForOrders"/>.</summary>
    public bool IsEstablished => string.Equals(SessionState, EstablishedState, StringComparison.Ordinal);
}

/// <summary>
/// DI seam for surfacing live FIXP session state into <c>/health</c> and
/// the frontend gateway badge. Implemented by <see cref="FirmGatewayRegistry"/>
/// in Real mode; absent in Mock/Stub/Unavailable modes (the lifecycle endpoint
/// treats the missing service as "no live wire to report on" and falls back
/// to <see cref="ExchangeStatus.ReadyForOrders"/> alone).
/// </summary>
public interface IFirmSessionStatusProvider
{
    /// <summary>Cheap snapshot of every firm's current session state.
    /// Safe to call on the request hot path; reads volatile fields and
    /// allocates a small array.</summary>
    IReadOnlyList<FirmSessionStatus> Snapshot();
}
