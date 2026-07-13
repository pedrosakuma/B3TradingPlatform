using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Hosting;
using B3.Trading.EntryPointListener.Tests.Mtls;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Tests.Integration;

/// <summary>
/// Sub-issue D (#540): end-to-end cert↔credential thumbprint binding at
/// Negotiate (RFC user-bot-fixp-mtls-v0 §4.3). Drives a real mTLS handshake
/// (presenting a client cert) followed by a FIXP Negotiate against the live
/// listener and asserts the pin is enforced.
/// </summary>
public sealed class FixpMtlsBindingTests : IDisposable
{
    private const ushort SchemaIdV6 = 1;

    private readonly string _dir;

    public FixpMtlsBindingTests()
    {
        _dir = Path.Combine(AppContext.BaseDirectory, "MtlsBinding", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed record HostBundle(
        IHost Host,
        FixpListenerHostedService Listener,
        InMemoryUserBotCredentialRegistry Credentials,
        InMemoryUserBotSessionRegistry Sessions);

    private string WriteServerPfx()
    {
        using var server = CertTestUtil.CreateCaCertificate("localhost");
        var path = Path.Combine(_dir, "server.pfx");
        File.WriteAllBytes(path, server.Export(X509ContentType.Pfx));
        return path;
    }

    private HostBundle BuildHost(ClientCertificateMode mode, string caBundlePath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:EntryPointListener:Enabled"] = "true",
                ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
                ["Trading:EntryPointListener:Tls:Required"] = "true",
                ["Trading:EntryPointListener:Tls:CertPath"] = WriteServerPfx(),
                ["Trading:EntryPointListener:Tls:ClientCertificateMode"] = mode.ToString(),
                ["Trading:EntryPointListener:Tls:ClientCa:BundlePath"] = caBundlePath,
                ["Trading:EntryPointListener:Tls:ClientCa:ReloadInterval"] = "00:05:00",
            })
            .Build();

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

        return new HostBundle(
            host,
            host.Services.GetRequiredService<FixpListenerHostedService>(),
            host.Services.GetRequiredService<InMemoryUserBotCredentialRegistry>(),
            host.Services.GetRequiredService<InMemoryUserBotSessionRegistry>());
    }

    private static async Task<SslStream> TlsConnectAsync(IPEndPoint ep, X509Certificate2? clientCert)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(ep);
        var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        var authOptions = new SslClientAuthenticationOptions { TargetHost = "localhost" };
        if (clientCert is not null)
            authOptions.ClientCertificates = new X509CertificateCollection { clientCert };
        await ssl.AuthenticateAsClientAsync(authOptions);
        return ssl;
    }

    private static byte[] BuildNegotiateFrame(uint sessionId, ulong sessionVerId, string token)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var bodyLen = NegotiateData.BLOCK_LENGTH + 1 + tokenBytes.Length;
        var body = new byte[bodyLen];
        ref var msg = ref MemoryMarshal.AsRef<NegotiateData>(body.AsSpan(0, NegotiateData.BLOCK_LENGTH));
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        body[NegotiateData.BLOCK_LENGTH] = (byte)tokenBytes.Length;
        tokenBytes.CopyTo(body, NegotiateData.BLOCK_LENGTH + 1);

        var frameSize = SofhFrameWriter.FrameSize(body.Length);
        var buf = new byte[frameSize];
        SofhFrameWriter.WriteFrame(buf,
            (ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID,
            SchemaIdV6, version: 6, body);
        return buf;
    }

    private static async Task<ushort?> ReadTemplateIdAsync(Stream stream, CancellationToken ct)
    {
        var reader = new SofhFrameReader();
        var buf = new byte[4096];
        while (true)
        {
            if (reader.TryReadFrame(out var frame))
                return frame.TemplateId;
            if (reader.HasProtocolError) return null;
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return null;
            reader.Append(buf.AsSpan(0, n));
        }
    }

    private async Task<ushort?> NegotiateOutcomeAsync(
        ClientCertificateMode mode,
        Func<X509Certificate2, (string? pin, X509Certificate2? presented)> arrange)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ca = CertTestUtil.CreateCaCertificate("B3 Bot CA");
        var bundle = CertTestUtil.WritePemBundle(_dir, "ca.pem", ca);
        var (pin, presented) = arrange(ca);

        var host = BuildHost(mode, bundle);
        try
        {
            await host.Host.StartAsync(cts.Token);
            var ep = await host.Listener.WhenBound.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            var created = await host.Credentials.CreateAsync("bot-user", "binding", pin, cts.Token);
            var state = await host.Sessions.GetOrCreateAsync(created.Credential.Id, cts.Token);

            using var ssl = await TlsConnectAsync(ep, presented);
            await ssl.WriteAsync(
                BuildNegotiateFrame(state.SessionId, state.CurrentVer, created.PlainToken), cts.Token);
            return await ReadTemplateIdAsync(ssl, cts.Token);
        }
        finally
        {
            presented?.Dispose();
            await host.Host.StopAsync();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task PinnedCredential_MatchingCert_NegotiateAccepted()
    {
        var tid = await NegotiateOutcomeAsync(ClientCertificateMode.Required, ca =>
        {
            var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-1");
            return (CertTestUtil.Sha256Thumbprint(leaf), leaf);
        });
        Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, tid);
    }

    [Fact(Timeout = 20_000)]
    public async Task PinnedCredential_WrongCert_NegotiateRejected()
    {
        var tid = await NegotiateOutcomeAsync(ClientCertificateMode.Required, ca =>
        {
            using var pinnedLeaf = CertTestUtil.CreateSignedLeaf(ca, "bot-pinned");
            var pin = CertTestUtil.Sha256Thumbprint(pinnedLeaf);
            // Present a *different* (but trusted) leaf — chain passes, pin fails.
            var presented = CertTestUtil.CreateSignedLeaf(ca, "bot-other");
            return (pin, presented);
        });
        Assert.Equal((ushort)NegotiateRejectData.MESSAGE_ID, tid);
    }

    [Fact(Timeout = 20_000)]
    public async Task UnpinnedCredential_AnyTrustedCert_NegotiateAccepted()
    {
        var tid = await NegotiateOutcomeAsync(ClientCertificateMode.Required, ca =>
        {
            var leaf = CertTestUtil.CreateSignedLeaf(ca, "bot-unpinned");
            return (null!, leaf); // null pin = unpinned
        });
        Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, tid);
    }

    [Fact(Timeout = 20_000)]
    public async Task Optional_PinnedCredential_NoCertPresented_NegotiateAccepted()
    {
        // RFC §4.3: under Optional the pin is enforced only when a cert is
        // presented; a certless connection still authenticates by PAT alone.
        var tid = await NegotiateOutcomeAsync(ClientCertificateMode.Optional, ca =>
        {
            using var pinnedLeaf = CertTestUtil.CreateSignedLeaf(ca, "bot-pinned");
            return (CertTestUtil.Sha256Thumbprint(pinnedLeaf), null);
        });
        Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, tid);
    }
}
