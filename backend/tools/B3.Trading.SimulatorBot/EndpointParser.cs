namespace B3.Trading.SimulatorBot;

/// <summary>Pure host:port parser. Returns a <see cref="System.Net.DnsEndPoint"/>
/// (no DNS resolution). The worker DNS-resolves at connect time
/// because matching-platform's docker hostname only exists on the
/// b3-net network.</summary>
public static class EndpointParser
{
    public static System.Net.DnsEndPoint Parse(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1)
            throw new ArgumentException($"Endpoint '{endpoint}' must be host:port.", nameof(endpoint));
        var host = endpoint[..colon];
        if (!int.TryParse(endpoint[(colon + 1)..], out var port) || port is <= 0 or > 65535)
            throw new ArgumentException($"Endpoint '{endpoint}' has invalid port.", nameof(endpoint));
        return new System.Net.DnsEndPoint(host, port);
    }
}
