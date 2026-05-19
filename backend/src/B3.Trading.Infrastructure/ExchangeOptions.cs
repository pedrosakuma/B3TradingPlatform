using B3.Trading.Application;

namespace B3.Trading.Infrastructure;

/// <summary>
/// #126. Selects the credential shape used in <see cref="FirmCredentialsConfig"/>.
/// Today the B3.EntryPoint.Client SDK (0.14.3) only exposes
/// <c>Credentials.FromUtf8(accessKey)</c>; this enum exists so future SDK
/// modes (certificate / token) can be added without re-shaping every
/// firm config in the wild.
/// </summary>
public enum FirmCredentialsMode
{
    /// <summary>Opaque access key passed via <c>Negotiate.Credentials</c> (the only mode supported by SDK 0.14.3).</summary>
    AccessKey,
}

/// <summary>
/// #126. Per-firm credential bundle. Replaces the loose
/// <see cref="FirmConfig.AccessKey"/> with a discriminated shape so secret
/// material can be loaded from indirection (file mount) and so new SDK
/// credential modes can be added without breaking deployments.
/// <para>
/// Exactly one secret source must be supplied for each mode:
/// <list type="bullet">
///   <item><see cref="AccessKey"/> — inline literal (back-compat / dev).</item>
///   <item><see cref="AccessKeyFile"/> — path read at startup (preferred for prod;
///         file must be 0600 / 0400 on Linux, see <see cref="FirmCredentialResolver"/>).</item>
/// </list>
/// </para>
/// </summary>
public sealed class FirmCredentialsConfig
{
    /// <summary>Discriminator. Only <see cref="FirmCredentialsMode.AccessKey"/> is wired today.</summary>
    public FirmCredentialsMode Mode { get; set; } = FirmCredentialsMode.AccessKey;

    /// <summary>Inline access-key literal. Mutually exclusive with <see cref="AccessKeyFile"/>.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Path to a file containing the access key (single line, trimmed). Mutually exclusive with <see cref="AccessKey"/>.</summary>
    public string? AccessKeyFile { get; set; }

    /// <summary>
    /// Sanitized projection — credential material is never printed. Test
    /// fixtures + the structured logger surface this shape so a config
    /// dump never leaks secret bytes.
    /// </summary>
    public override string ToString() =>
        $"FirmCredentialsConfig {{ Mode = {Mode}, AccessKey = {Redact(AccessKey)}, AccessKeyFile = {AccessKeyFile ?? "<null>"} }}";

    private static string Redact(string? value) =>
        string.IsNullOrEmpty(value) ? "<null>" : $"<redacted:{value!.Length}>";
}

/// <summary>
/// Per-firm FIXP session configuration. One instance per firm represented
/// on the platform (1 platform → N FIXP sessions, see issue #1 §1).
///
/// Field shapes mirror <c>EntryPointClientOptions</c> from the upstream
/// <c>B3.EntryPoint.Client</c> package so binding is a 1:1 copy at startup
/// when <see cref="ExchangeOptions.UseRealEntryPointClient"/> is enabled.
/// </summary>
public sealed class FirmConfig
{
    /// <summary>Logical firm identifier used as the JWT <c>firm</c> claim and as the routing key in <c>FirmGatewayRegistry</c>.</summary>
    public string FirmId { get; set; } = string.Empty;

    /// <summary>Gateway endpoint as <c>host:port</c> (parsed into <c>IPEndPoint</c>). Required in real-client mode.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Client connection identification on the gateway, assigned by B3.</summary>
    public uint SessionId { get; set; }

    /// <summary>Session version identification — must increase on each new Negotiate.</summary>
    public uint SessionVerId { get; set; }

    /// <summary>Identifies the broker firm that will enter orders.</summary>
    public uint EnteringFirm { get; set; }

    /// <summary>
    /// Legacy. Opaque access key sent in <c>Negotiate.Credentials</c> via
    /// <c>Credentials.FromUtf8</c>. Kept for back-compat with existing
    /// deployments; new configs should use <see cref="Credentials"/>. When
    /// both are set, <see cref="Credentials"/> wins and the legacy value
    /// is ignored after a startup WARN.
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// #126. Discriminated credential bundle. When set, wins over the
    /// legacy <see cref="AccessKey"/> field and supports file-mounted
    /// secret indirection (preferred for production).
    /// </summary>
    public FirmCredentialsConfig? Credentials { get; set; }

    /// <summary>FIX <c>SenderLocation</c> (max 10 chars). Per-firm default; not threaded per-order in v1.</summary>
    public string SenderLocation { get; set; } = string.Empty;

    /// <summary>FIX <c>EnteringTrader</c> (max 5 chars). Per-firm default; not threaded per-order in v1.</summary>
    public string EnteringTrader { get; set; } = string.Empty;

    /// <summary>FIXP keep-alive interval requested by the client (ms).</summary>
    public uint KeepAliveIntervalMs { get; set; } = 1000u;
}

/// <summary>
/// Selects which <c>IExchangeGateway</c> the Host wires.
/// </summary>
public enum ExchangeMode
{
    /// <summary>No-op <see cref="StubExchangeGateway"/>. Submits succeed silently. CI / smoke.</summary>
    Stub,

    /// <summary>In-process <c>MockEntryPointClient</c> + <c>EntryPointClientGateway</c>. No TCP. Dev loop and integration tests. When paired with <see cref="ExchangeOptions.AllowErInjection"/>=<c>true</c>, the admin-gated <c>POST /admin/simulator/er</c> endpoint is mapped (formerly the standalone <c>Simulator</c> variant; merged into Mock in #163).</summary>
    Mock,

    /// <summary>Real <c>B3.EntryPoint.Client.EntryPointClient</c> per <see cref="FirmConfig"/> behind <c>MultiFirmExchangeGateway</c>.</summary>
    Real,

    /// <summary>
    /// Fail-closed: every submit/cancel/replace throws so <c>OrdersEndpoints</c>
    /// synthesizes a rejection and returns 502. Use in production-like
    /// containers when no broker is wired yet (Docker bootstrap, isolation
    /// drills); the API stays up and honest instead of silently accepting
    /// orders that nothing on the wire will ever match.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Bound from the <c>Trading:Exchange</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class ExchangeOptions
{
    public const string SectionName = "Trading:Exchange";

    /// <summary>
    /// Explicit mode selector. Wins over the legacy <see cref="UseStubGateway"/>
    /// / <see cref="UseRealEntryPointClient"/> flags when set. When null, the
    /// legacy flags decide (Stub if <see cref="UseStubGateway"/>; Real if
    /// <see cref="UseRealEntryPointClient"/>; Mock otherwise) for backward
    /// compatibility with existing deployments.
    /// </summary>
    public ExchangeMode? Mode { get; set; }

    /// <summary>
    /// Legacy flag. Prefer <see cref="Mode"/> = <see cref="ExchangeMode.Stub"/>.
    /// </summary>
    public bool UseStubGateway { get; set; }

    /// <summary>
    /// Legacy flag. Prefer <see cref="Mode"/> = <see cref="ExchangeMode.Real"/>.
    /// </summary>
    public bool UseRealEntryPointClient { get; set; }

    public List<FirmConfig> Firms { get; set; } = new();

    /// <summary>
    /// When <c>true</c> AND <see cref="ResolveMode"/> is
    /// <see cref="ExchangeMode.Mock"/>, the admin-gated
    /// <c>POST /admin/simulator/er</c> endpoint is mapped so test harnesses
    /// can inject synthetic execution reports for any working
    /// <c>ClOrdId</c>. Required by Iceberg / TWAP integration + conformance
    /// tests; replaces the legacy <c>ExchangeMode.Simulator</c> variant
    /// (#163).
    /// <para>
    /// Refused for any non-Mock mode (validated at startup). Refused in
    /// Production unless <see cref="AllowErInjectionInProduction"/> is also
    /// <c>true</c> — synthetic ER injection has catastrophic blast radius
    /// if it leaks into a real-money deployment.
    /// </para>
    /// </summary>
    public bool AllowErInjection { get; set; }

    /// <summary>
    /// Production opt-out for <see cref="AllowErInjection"/>. When
    /// <c>false</c> (default), the host refuses to boot if ER injection is
    /// enabled while <c>Environment=Production</c>. Set to <c>true</c> only
    /// for explicit production-shaped sandboxes that have no real-money risk.
    /// </summary>
    public bool AllowErInjectionInProduction { get; set; }

    /// <summary>
    /// Resolves the effective mode: explicit <see cref="Mode"/> if set, else
    /// the legacy flag mapping (default = <see cref="ExchangeMode.Mock"/>).
    /// </summary>
    public ExchangeMode ResolveMode() =>
        Mode ?? (UseStubGateway ? ExchangeMode.Stub
              : UseRealEntryPointClient ? ExchangeMode.Real
              : ExchangeMode.Mock);
}
