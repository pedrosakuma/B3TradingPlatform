using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_UserBot;

/// <summary>
/// mTLS conformance (RFC user-bot-fixp-mtls-v0 §8). Profile-gated: runs only
/// when an mTLS-enabled listener and a trusted client PFX are configured via
/// <c>B3T_FIXP_ENDPOINT</c> + <c>B3T_FIXP_MTLS_CLIENT_PFX</c>. The two black-box
/// admit/reject rows that need only operator-side material (a trusted leaf, or
/// no cert) run here against the real listener; the wrong-CA / time-invalid /
/// denied-thumbprint / wrong-pin / hot-reload rows are covered by the in-proc
/// suite in <c>B3.Trading.EntryPointListener.Tests</c> (MtlsHandshakeTests +
/// FixpMtlsBindingTests), which can synthesize the cert material and own the
/// listener's trust config. Per AGENTS.md, failures here are real regressions.
/// </summary>
[Trait("Category", "Conformance")]
public class FixpMtlsConformanceSpecTests
{
    [ConformanceFact(RequiresFixpMtls = true)]
    public async Task TrustedClientCert_HandshakeAdmitted()
    {
        var (host, port) = ResolveEndpoint();
        var pfx = LoadClientPfx();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ClientCertificates = new X509CertificateCollection { pfx },
        });

        // A trusted leaf is admitted by Required-mode mTLS: handshake completes
        // and the connection stays open awaiting Negotiate.
        Assert.True(await ProbeAdmittedAsync(ssl));
    }

    [ConformanceFact(RequiresFixpMtls = true)]
    public async Task NoClientCert_RequiredMode_HandshakeRejected()
    {
        var (host, port) = ResolveEndpoint();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host });
            // TLS 1.3 may complete the handshake then drop on missing cert.
            Assert.False(await ProbeAdmittedAsync(ssl));
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            // Server aborted the handshake — the expected Required-mode reject.
        }
    }

    private static async Task<bool> ProbeAdmittedAsync(SslStream ssl)
    {
        var buf = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try
        {
            var n = await ssl.ReadAsync(buf, cts.Token);
            return n > 0; // bytes flowing = admitted (rejected peers send 0)
        }
        catch (OperationCanceledException)
        {
            return true; // open, idle = admitted
        }
        catch (IOException)
        {
            return false; // reset = rejected
        }
    }

    private static (string Host, int Port) ResolveEndpoint()
    {
        var ep = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpEndpoint)!;
        var lastColon = ep.LastIndexOf(':');
        int port = 0;
        var ok = lastColon > 0 && int.TryParse(ep[(lastColon + 1)..], out port);
        Assert.True(ok, $"{PlatformEndpoint.EnvFixpEndpoint} must be host:port, got '{ep}'.");
        var host = ep[..lastColon].Trim('[', ']'); // tolerate bracketed IPv6
        return (host, port);
    }

    private static X509Certificate2 LoadClientPfx()
    {
        var path = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpMtlsClientPfx)!;
        var pass = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpMtlsClientPfxPass);
        return X509CertificateLoader.LoadPkcs12FromFile(path, pass);
    }
}
