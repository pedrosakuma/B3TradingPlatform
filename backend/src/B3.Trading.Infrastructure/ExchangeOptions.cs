namespace B3.Trading.Infrastructure;

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

    /// <summary>Opaque access key sent in <c>Negotiate.Credentials</c> via <c>Credentials.FromUtf8</c>.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>FIX <c>SenderLocation</c> (max 10 chars). Per-firm default; not threaded per-order in v1.</summary>
    public string SenderLocation { get; set; } = string.Empty;

    /// <summary>FIX <c>EnteringTrader</c> (max 5 chars). Per-firm default; not threaded per-order in v1.</summary>
    public string EnteringTrader { get; set; } = string.Empty;

    /// <summary>FIXP keep-alive interval requested by the client (ms).</summary>
    public uint KeepAliveIntervalMs { get; set; } = 1000u;
}

/// <summary>
/// Bound from the <c>Trading:Exchange</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class ExchangeOptions
{
    public const string SectionName = "Trading:Exchange";

    /// <summary>
    /// When true, the Host wires the no-op <see cref="StubExchangeGateway"/>
    /// instead of any real gateway. Useful for API-only smoke tests and CI
    /// without any FIXP plumbing.
    /// </summary>
    public bool UseStubGateway { get; set; }

    /// <summary>
    /// When true (and <see cref="UseStubGateway"/> is false), the Host
    /// instantiates a real <c>B3.EntryPoint.Client.EntryPointClient</c>
    /// per <see cref="FirmConfig"/> and routes through
    /// <see cref="MultiFirmExchangeGateway"/>. When false, the in-process
    /// <see cref="MockEntryPointClient"/> is wired (no TCP, no real session).
    /// </summary>
    public bool UseRealEntryPointClient { get; set; }

    public List<FirmConfig> Firms { get; set; } = new();
}
