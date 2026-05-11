namespace B3.Trading.Application.Lifecycle;

/// <summary>
/// Single per-firm row exposed by <see cref="IFirmDirectory"/>. Carries the
/// configured wire identity (always present) plus optional live FIXP session
/// state that the Real-mode implementation overlays from
/// <c>FirmGatewayRegistry</c>. Mock/Stub/Unavailable implementations leave
/// the state-tagged fields null, which the admin endpoint surfaces as
/// "configured but no live wire to report on".
/// </summary>
/// <param name="FirmId">Logical firm identifier (matches <c>FirmConfig.FirmId</c>).</param>
/// <param name="Endpoint">Gateway endpoint as <c>host:port</c>.</param>
/// <param name="SessionId">Client connection identification on the gateway.</param>
/// <param name="SessionState">Lower-snake-case tag of the live SDK
/// <c>FixpClientState</c> when available; null otherwise.</param>
/// <param name="SessionVerId">Last <c>SessionVerId</c> the gateway has tried
/// to use; null when no live status is available.</param>
/// <param name="Reconnecting">True when the gateway's auto-reconnect loop
/// is currently running for this firm; null when no live status is available.</param>
public sealed record FirmDirectoryEntry(
    string FirmId,
    string Endpoint,
    uint SessionId,
    string? SessionState,
    uint? SessionVerId,
    bool? Reconnecting);

/// <summary>
/// Aggregate snapshot returned by <see cref="IFirmDirectory.Snapshot"/>.
/// </summary>
/// <param name="Mode">Effective <c>ExchangeMode</c> name (e.g. <c>Real</c>,
/// <c>Mock</c>) — surfaced verbatim on <c>/admin/firms</c>.</param>
/// <param name="Firms">Per-firm entries; empty in <c>Unavailable</c> mode.</param>
public sealed record FirmDirectorySnapshot(
    string Mode,
    IReadOnlyList<FirmDirectoryEntry> Firms);

/// <summary>
/// Application-layer port that the <c>/admin/firms</c> endpoint consumes to
/// list per-firm wire configuration plus optional live FIXP session state.
/// Decouples the Api layer from the Infrastructure-owned
/// <c>FirmGatewayRegistry</c> / <c>ExchangeOptions</c> concretions.
/// </summary>
public interface IFirmDirectory
{
    /// <summary>Cheap, allocation-light snapshot of every configured firm.
    /// Safe to call on the request hot path.</summary>
    FirmDirectorySnapshot Snapshot();
}
