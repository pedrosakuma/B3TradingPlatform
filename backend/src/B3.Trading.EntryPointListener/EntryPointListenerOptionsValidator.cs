using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener;

/// <summary>
/// Eager-fail validation for <see cref="EntryPointListenerOptions"/>.
/// Registered via <see cref="EntryPointListenerServiceCollectionExtensions.AddEntryPointListener"/>.
/// </summary>
public sealed class EntryPointListenerOptionsValidator : IValidateOptions<EntryPointListenerOptions>
{
    public ValidateOptionsResult Validate(string? name, EntryPointListenerOptions options)
    {
        if (options is null)
            return ValidateOptionsResult.Fail("EntryPointListenerOptions is null.");

        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            return ValidateOptionsResult.Fail(
                "Trading:EntryPointListener:Endpoint must be set when Enabled=true.");

        if (!TryParseEndpoint(options.Endpoint, out _))
            return ValidateOptionsResult.Fail(
                $"Trading:EntryPointListener:Endpoint '{options.Endpoint}' is not a valid " +
                "IP-literal endpoint. Use 'ip:port', '*:port', or '[ipv6]:port'. DNS names are not accepted.");

        var failures = new List<string>();

        // Rate limit validation
        if (options.RateLimit.NegotiatesPerMinutePerIp <= 0)
            failures.Add("Trading:EntryPointListener:RateLimit:NegotiatesPerMinutePerIp must be > 0.");
        if (options.RateLimit.NegotiatesPerMinutePerUsername <= 0)
            failures.Add("Trading:EntryPointListener:RateLimit:NegotiatesPerMinutePerUsername must be > 0.");

        // MaxSessionsPerUser validation
        if (options.MaxSessionsPerUser <= 0)
            failures.Add("Trading:EntryPointListener:MaxSessionsPerUser must be > 0.");
        if (options.RetransmitTimeoutMs <= 0)
            failures.Add("Trading:EntryPointListener:RetransmitTimeoutMs must be > 0.");

        // #529 public-hardening: caps + IP filter + handshake timeout
        if (options.ConnectionCaps.MaxConcurrentTotal < 0)
            failures.Add("Trading:EntryPointListener:ConnectionCaps:MaxConcurrentTotal must be >= 0.");
        if (options.ConnectionCaps.MaxConcurrentPerIp < 0)
            failures.Add("Trading:EntryPointListener:ConnectionCaps:MaxConcurrentPerIp must be >= 0.");
        if (options.Tls.HandshakeTimeout <= TimeSpan.Zero)
            failures.Add("Trading:EntryPointListener:Tls:HandshakeTimeout must be > 0.");
        if (options.AcceptRateLimit.ConnectionsPerSecondPerIp < 0)
            failures.Add("Trading:EntryPointListener:AcceptRateLimit:ConnectionsPerSecondPerIp must be >= 0.");
        if (options.AcceptRateLimit.BurstPerIp <= 0)
            failures.Add("Trading:EntryPointListener:AcceptRateLimit:BurstPerIp must be > 0.");
        foreach (var ip in options.ConnectionCaps.AllowedIps.Concat(options.ConnectionCaps.DeniedIps))
            if (!System.Net.IPAddress.TryParse(ip, out _))
                failures.Add($"Trading:EntryPointListener:ConnectionCaps allow/deny entry '{ip}' is not a valid IP.");

        // TCP tunables (RFC §5.9 / P11)
        if (options.Tcp.SendBufferBytes <= 0)
            failures.Add("Trading:EntryPointListener:Tcp:SendBufferBytes must be > 0.");
        if (options.Tcp.ReceiveBufferBytes <= 0)
            failures.Add("Trading:EntryPointListener:Tcp:ReceiveBufferBytes must be > 0.");
        if (options.Buffers.OutboundRingSize <= 0)
            failures.Add("Trading:EntryPointListener:Buffers:OutboundRingSize must be > 0.");
        if (options.Buffers.MappingReapAfter <= TimeSpan.Zero)
            failures.Add("Trading:EntryPointListener:Buffers:MappingReapAfter must be > 0.");
        if (options.Buffers.OutboundChannelCapacity <= 0)
            failures.Add("Trading:EntryPointListener:Buffers:OutboundChannelCapacity must be > 0.");
        if (options.Buffers.OutboundDrainShutdownTimeout <= TimeSpan.Zero)
            failures.Add("Trading:EntryPointListener:Buffers:OutboundDrainShutdownTimeout must be > 0.");

        // TLS validation
        if (options.Tls.Required)
        {
            if (string.IsNullOrWhiteSpace(options.Tls.CertPath))
                failures.Add("Trading:EntryPointListener:Tls:CertPath must be set when Tls:Required=true.");
            if (!options.Tls.IsPfx && string.IsNullOrWhiteSpace(options.Tls.KeyPath))
                failures.Add("Trading:EntryPointListener:Tls:KeyPath must be set when Tls:Required=true and CertPath is PEM (not .pfx/.p12).");
            if (!string.IsNullOrWhiteSpace(options.Tls.CertPath) && !File.Exists(options.Tls.CertPath))
                failures.Add($"Trading:EntryPointListener:Tls:CertPath '{options.Tls.CertPath}' does not exist.");
            if (!string.IsNullOrWhiteSpace(options.Tls.KeyPath) && !File.Exists(options.Tls.KeyPath))
                failures.Add($"Trading:EntryPointListener:Tls:KeyPath '{options.Tls.KeyPath}' does not exist.");
        }

        // mTLS / client-certificate validation (RFC user-bot-fixp-mtls-v0 §5.1)
        if (options.Tls.MtlsEnabled)
        {
            if (!options.Tls.Required)
                failures.Add(
                    "Trading:EntryPointListener:Tls:ClientCertificateMode is " +
                    $"'{options.Tls.ClientCertificateMode}' but Tls:Required=false. " +
                    "mTLS requires TLS — set Tls:Required=true.");

            if (options.Tls.ClientCa.ReloadInterval <= TimeSpan.Zero)
                failures.Add("Trading:EntryPointListener:Tls:ClientCa:ReloadInterval must be > 0.");

            if (string.IsNullOrWhiteSpace(options.Tls.ClientCa.BundlePath))
            {
                failures.Add(
                    "Trading:EntryPointListener:Tls:ClientCa:BundlePath must be set when " +
                    "Tls:ClientCertificateMode is Optional or Required.");
            }
            else
            {
                // Fail closed at boot on a missing / unparseable bundle, so a
                // misconfigured trust anchor never reaches the handshake gate.
                try
                {
                    _ = Mtls.ClientCaTrustProvider.LoadTrustAnchors(options.Tls.ClientCa.BundlePath);
                }
                catch (Exception ex)
                {
                    failures.Add(
                        "Trading:EntryPointListener:Tls:ClientCa:BundlePath " +
                        $"'{options.Tls.ClientCa.BundlePath}' is not a usable PEM CA bundle: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(options.Tls.ClientCa.DenyListPath) &&
                !File.Exists(options.Tls.ClientCa.DenyListPath))
            {
                failures.Add(
                    "Trading:EntryPointListener:Tls:ClientCa:DenyListPath " +
                    $"'{options.Tls.ClientCa.DenyListPath}' does not exist.");
            }
            else if (!string.IsNullOrWhiteSpace(options.Tls.ClientCa.DenyListPath))
            {
                // Fail closed at boot on a malformed deny-list so a typo'd or
                // stale-SHA-1 thumbprint never silently fails to revoke.
                try
                {
                    _ = Mtls.ClientCaTrustProvider.LoadDenyList(
                        options.Tls.ClientCa.DenyListPath, out var invalidLines);
                    if (invalidLines > 0)
                        failures.Add(
                            "Trading:EntryPointListener:Tls:ClientCa:DenyListPath " +
                            $"'{options.Tls.ClientCa.DenyListPath}' has {invalidLines} malformed " +
                            "entr(y/ies); each must be a 64-character SHA-256 hex thumbprint.");
                }
                catch (Exception ex)
                {
                    failures.Add(
                        "Trading:EntryPointListener:Tls:ClientCa:DenyListPath " +
                        $"'{options.Tls.ClientCa.DenyListPath}' could not be read: {ex.Message}");
                }
            }
        }

        if (failures.Count > 0)
            return ValidateOptionsResult.Fail(failures);

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Parses an endpoint string into an <see cref="IPEndPoint"/>.
    /// Accepts IPv4 literals (<c>192.168.1.1:5001</c>), IPv6 literals
    /// (<c>[::1]:5001</c>), wildcard (<c>*:5001</c> / <c>0.0.0.0:5001</c>
    /// / <c>:::5001</c>), and port zero for OS-assigned ports.
    /// DNS names are explicitly rejected.
    /// </summary>
    public static bool TryParseEndpoint(
        string? input,
        [NotNullWhen(true)] out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string host;
        int port;

        if (input.StartsWith('['))
        {
            var closeBracket = input.IndexOf(']', StringComparison.Ordinal);
            if (closeBracket < 0) return false;
            if (closeBracket + 1 >= input.Length || input[closeBracket + 1] != ':') return false;
            host = input[1..closeBracket];
            if (!int.TryParse(input[(closeBracket + 2)..], out port)) return false;
        }
        else
        {
            var lastColon = input.LastIndexOf(':');
            if (lastColon < 0) return false;
            host = input[..lastColon];
            if (!int.TryParse(input[(lastColon + 1)..], out port)) return false;
        }

        if (port is < 0 or > 65535) return false;

        if (host is "*" or "0.0.0.0")
        {
            endpoint = new IPEndPoint(IPAddress.Any, port);
            return true;
        }

        if (host is "::")
        {
            endpoint = new IPEndPoint(IPAddress.IPv6Any, port);
            return true;
        }

        if (!IPAddress.TryParse(host, out var addr)) return false;

        endpoint = new IPEndPoint(addr, port);
        return true;
    }
}
