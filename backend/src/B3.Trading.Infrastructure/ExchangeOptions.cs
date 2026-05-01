namespace B3.Trading.Infrastructure;

/// <summary>
/// Per-firm FIXP session configuration. One instance per firm represented
/// on the platform (1 platform → N FIXP sessions, see issue #1 §1).
/// </summary>
public sealed class FirmConfig
{
    public string FirmId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string SenderCompId { get; set; } = string.Empty;
    public string TargetCompId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Bound from the <c>Trading:Exchange</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class ExchangeOptions
{
    public const string SectionName = "Trading:Exchange";

    /// <summary>
    /// When true, the Host wires the no-op <see cref="StubExchangeGateway"/>
    /// instead of the real <see cref="EntryPointClientGateway"/>. Useful for
    /// API-only smoke tests and CI without any FIXP plumbing.
    /// </summary>
    public bool UseStubGateway { get; set; }

    public List<FirmConfig> Firms { get; set; } = new();
}
