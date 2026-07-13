using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using B3.Trading.EntryPointListener.Tests.Mtls;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

/// <summary>
/// End-to-end coverage of the mTLS handshake gate (RFC user-bot-fixp-mtls-v0
/// §6): a client cert is validated at the TLS layer before any FIXP byte is
/// processed. Drives a real <see cref="SslStream"/> client against the live
/// listener.
/// </summary>
public sealed class MtlsHandshakeTests : IDisposable
{
    private readonly string _dir;

    public MtlsHandshakeTests()
    {
        _dir = Path.Combine(AppContext.BaseDirectory, "MtlsCerts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteServerPfx()
    {
        using var server = CertTestUtil.CreateCaCertificate("localhost");
        var path = Path.Combine(_dir, "server.pfx");
        File.WriteAllBytes(path, server.Export(X509ContentType.Pfx));
        return path;
    }

    private async Task<IHost> StartHostAsync(
        ClientCertificateMode mode, string caBundlePath, string? denyListPath = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Trading:EntryPointListener:Enabled"] = "true",
            ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
            ["Trading:EntryPointListener:Tls:Required"] = "true",
            ["Trading:EntryPointListener:Tls:CertPath"] = WriteServerPfx(),
            ["Trading:EntryPointListener:Tls:ClientCertificateMode"] = mode.ToString(),
            ["Trading:EntryPointListener:Tls:ClientCa:BundlePath"] = caBundlePath,
            ["Trading:EntryPointListener:Tls:ClientCa:ReloadInterval"] = "00:05:00",
        };
        if (denyListPath is not null)
            settings["Trading:EntryPointListener:Tls:ClientCa:DenyListPath"] = denyListPath;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var host = new HostBuilder()
            .ConfigureServices((_, s) =>
            {
                s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                s.AddSingleton<InMemoryUserBotCredentialRegistry>();
                s.AddSingleton<IUserBotCredentialRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotCredentialRegistry>());
                s.AddSingleton<InMemoryUserBotSessionRegistry>();
                s.AddSingleton<IUserBotSessionRegistry>(sp =>
                    sp.GetRequiredService<InMemoryUserBotSessionRegistry>());
                s.AddNoopOrderPathStubs();
                s.AddEntryPointListener(config);
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Returns true when the listener <em>admits</em> the connection, false
    /// when it rejects the client certificate. Version-agnostic: under TLS 1.3
    /// (and some 1.2 stacks) client-cert rejection surfaces only after the
    /// client's handshake returns — when the server aborts/closes the
    /// connection — so we probe by reading. An admitted connection stays open
    /// waiting for the first FIXP frame (read times out); a rejected one is
    /// closed or reset (read returns 0 / throws).
    /// </summary>
    private static async Task<bool> ClientAdmittedAsync(
        IPEndPoint ep, X509Certificate2? clientCert)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);

        var authOptions = new SslClientAuthenticationOptions { TargetHost = "localhost" };
        if (clientCert is not null)
            authOptions.ClientCertificates = new X509CertificateCollection { clientCert };

        try
        {
            await ssl.AuthenticateAsClientAsync(authOptions);
        }
        catch
        {
            return false; // in-handshake rejection (TLS 1.2 path)
        }

        if (!ssl.IsAuthenticated)
            return false;

        var buf = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try
        {
            var n = await ssl.ReadAsync(buf, cts.Token);
            return n != 0; // 0 = server closed = rejected
        }
        catch (OperationCanceledException)
        {
            return true; // connection still open = admitted
        }
        catch
        {
            return false; // reset / TLS alert = rejected
        }
    }

    [Fact]
    public async Task Required_TrustedClientCert_HandshakeSucceeds()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
        using var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-1");

        var host = await StartHostAsync(ClientCertificateMode.Required, bundle);
        try
        {
            var ep = await host.Services.GetRequiredService<FixpListenerHostedService>().WhenBound;
            Assert.True(await ClientAdmittedAsync(ep, leaf));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Required_NoClientCert_HandshakeFails()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);

        var host = await StartHostAsync(ClientCertificateMode.Required, bundle);
        try
        {
            var ep = await host.Services.GetRequiredService<FixpListenerHostedService>().WhenBound;
            Assert.False(await ClientAdmittedAsync(ep, clientCert: null));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Required_UntrustedCa_HandshakeFails()
    {
        using var trustedCa = CertTestUtil.CreateCaCertificate("B3 Trusted CA");
        using var rogueCa = CertTestUtil.CreateCaCertificate("Rogue CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", trustedCa);
        using var rogueLeaf = CertTestUtil.CreateSignedLeaf(rogueCa, "bot-rogue");

        var host = await StartHostAsync(ClientCertificateMode.Required, bundle);
        try
        {
            var ep = await host.Services.GetRequiredService<FixpListenerHostedService>().WhenBound;
            Assert.False(await ClientAdmittedAsync(ep, rogueLeaf));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Required_DeniedThumbprint_HandshakeFails()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
        using var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-revoked");
        var denyPath = Path.Combine(_dir, "deny.txt");
        File.WriteAllText(denyPath, CertTestUtil.Sha256Thumbprint(leaf) + Environment.NewLine);

        var host = await StartHostAsync(ClientCertificateMode.Required, bundle, denyPath);
        try
        {
            var ep = await host.Services.GetRequiredService<FixpListenerHostedService>().WhenBound;
            Assert.False(await ClientAdmittedAsync(ep, leaf));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Optional_NoClientCert_HandshakeSucceeds()
    {
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);

        var host = await StartHostAsync(ClientCertificateMode.Optional, bundle);
        try
        {
            var ep = await host.Services.GetRequiredService<FixpListenerHostedService>().WhenBound;
            Assert.True(await ClientAdmittedAsync(ep, clientCert: null));
        }
        finally { await host.StopAsync(); }
    }
}
