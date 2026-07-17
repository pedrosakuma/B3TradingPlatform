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
        var (host, port) = SplitHostPort(endpoint);
        var addrs = System.Net.Dns.GetHostAddresses(host);
        return ToEndPoint(host, port, addrs);
    }

    /// <summary>
    /// Produces a construction-time placeholder without performing DNS I/O.
    /// The gateway replaces it through <see cref="ParseEndpointAsync"/> inside
    /// the serialized cold-connect/reconnect attempt.
    /// </summary>
    public static System.Net.IPEndPoint CreateUnresolvedEndpoint(string endpoint)
    {
        var (_, port) = SplitHostPort(endpoint);
        return new System.Net.IPEndPoint(System.Net.IPAddress.None, port);
    }

    /// <summary>
    /// Async, cancellable counterpart of <see cref="ParseEndpoint"/>. Used by
    /// <see cref="B3.Trading.Infrastructure.B3EntryPointClientGateway"/>'s
    /// reconnect loop (#565) to re-resolve the peer hostname before every
    /// reconnect attempt WITHOUT blocking a thread-pool thread on the
    /// synchronous <c>Dns.GetHostAddresses</c> overload — that matters
    /// precisely during the network incidents (pod reschedule, CoreDNS
    /// blips) this feature targets, where a hung resolver could otherwise
    /// starve the pool or ignore shutdown.
    /// </summary>
    public static async System.Threading.Tasks.Task<System.Net.IPEndPoint> ParseEndpointAsync(
        string endpoint, System.Threading.CancellationToken ct = default)
    {
        var (host, port) = SplitHostPort(endpoint);
        var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return ToEndPoint(host, port, addrs);
    }

    private static (string Host, int Port) SplitHostPort(string endpoint)
    {
        var parts = endpoint.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new FormatException($"FirmConfig.Endpoint must be 'host:port', got '{endpoint}'.");
        return (parts[0], port);
    }

    private static System.Net.IPEndPoint ToEndPoint(string host, int port, System.Net.IPAddress[] addrs)
    {
        if (addrs.Length == 0)
            throw new FormatException($"Could not resolve '{host}'.");
        return new System.Net.IPEndPoint(addrs[0], port);
    }
}
