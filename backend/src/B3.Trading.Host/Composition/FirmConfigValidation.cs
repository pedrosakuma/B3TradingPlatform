namespace B3.Trading.Host.Composition;

internal static class FirmConfigValidation
{
    /// <summary>
    /// DNS-resolves <paramref name="endpoint"/> in <c>host:port</c> form into
    /// an <see cref="System.Net.IPEndPoint"/>. Shape validation lives in
    /// <c>ExchangeOptionsValidator</c>; this helper is invoked at first
    /// DI resolution by the Real-mode factory because it needs network access
    /// and shouldn't block <c>ValidateOnStart</c>.
    /// </summary>
    public static System.Net.IPEndPoint ParseEndpoint(string endpoint)
    {
        var parts = endpoint.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new FormatException($"FirmConfig.Endpoint must be 'host:port', got '{endpoint}'.");
        var addrs = System.Net.Dns.GetHostAddresses(parts[0]);
        if (addrs.Length == 0)
            throw new FormatException($"Could not resolve '{parts[0]}'.");
        return new System.Net.IPEndPoint(addrs[0], port);
    }
}
