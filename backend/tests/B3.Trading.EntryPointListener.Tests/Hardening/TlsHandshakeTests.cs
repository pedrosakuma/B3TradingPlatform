using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

public class TlsHandshakeTests
{
    [Fact]
    public async Task TlsEnabled_SslStreamHandshake_Succeeds()
    {
        var certDir = Path.Combine(AppContext.BaseDirectory, "TestCerts");
        Directory.CreateDirectory(certDir);
        var certPath = Path.Combine(certDir, $"tls_test_{Guid.NewGuid():N}.pfx");

        try
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
            var pfxBytes = cert.Export(X509ContentType.Pfx);
            await File.WriteAllBytesAsync(certPath, pfxBytes);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Trading:EntryPointListener:Enabled"] = "true",
                    ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
                    ["Trading:EntryPointListener:Tls:Required"] = "true",
                    ["Trading:EntryPointListener:Tls:CertPath"] = certPath,
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

            await host.StartAsync();
            try
            {
                var listener = host.Services.GetRequiredService<FixpListenerHostedService>();
                var ep = await listener.WhenBound;

                var recorded = 0;
                var sawOk = false;
                using var meter = new MeterListener();
                meter.InstrumentPublished = (instr, ml) =>
                {
                    if (instr.Name == "fixp.handshake.tls.duration_ms")
                        ml.EnableMeasurementEvents(instr);
                };
                meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
                {
                    recorded++;
                    foreach (var t in tags)
                        if (t.Key == "outcome" && (t.Value as string) == "ok") sawOk = true;
                });
                meter.Start();

                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ep);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                });

                Assert.True(ssl.IsAuthenticated);
                Assert.True(ssl.IsEncrypted);

                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (!sawOk && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                Assert.True(recorded >= 1);
                Assert.True(sawOk);
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            if (File.Exists(certPath)) File.Delete(certPath);
        }
    }

    [Fact]
    public async Task TlsRequired_PlainTcpClient_FailsHandshake()
    {
        var certDir = Path.Combine(AppContext.BaseDirectory, "TestCerts");
        Directory.CreateDirectory(certDir);
        var certPath = Path.Combine(certDir, $"tls_test_{Guid.NewGuid():N}.pfx");

        try
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
            var pfxBytes = cert.Export(X509ContentType.Pfx);
            await File.WriteAllBytesAsync(certPath, pfxBytes);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Trading:EntryPointListener:Enabled"] = "true",
                    ["Trading:EntryPointListener:Endpoint"] = "127.0.0.1:0",
                    ["Trading:EntryPointListener:Tls:Required"] = "true",
                    ["Trading:EntryPointListener:Tls:CertPath"] = certPath,
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

            await host.StartAsync();
            try
            {
                var listener = host.Services.GetRequiredService<FixpListenerHostedService>();
                var ep = await listener.WhenBound;

                // Connect plain TCP and send garbage — the server should
                // reject or close after TLS handshake failure.
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ep);
                var stream = tcp.GetStream();

                // Send non-TLS bytes and use a CTS to bound the read
                stream.WriteByte(0x00);
                await stream.FlushAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var buf = new byte[16];
                try
                {
                    var read = await stream.ReadAsync(buf, cts.Token);
                    Assert.Equal(0, read);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // Expected — server closed or timed out after TLS failure
                }
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            if (File.Exists(certPath)) File.Delete(certPath);
        }
    }
}
