using System.Net;
using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

public class ConnectionGateTests
{
    private static EntryPointListenerOptions.ConnectionCapsOptions Caps(
        int total = 0, int perIp = 0, string[]? allow = null, string[]? deny = null) => new()
        {
            MaxConcurrentTotal = total,
            MaxConcurrentPerIp = perIp,
            AllowedIps = allow?.ToList() ?? new List<string>(),
            DeniedIps = deny?.ToList() ?? new List<string>(),
        };

    [Fact]
    public void Unlimited_ByDefault_AlwaysAcquires()
    {
        var gate = new ConnectionGate(Caps());
        for (int i = 0; i < 100; i++)
            Assert.True(gate.TryAcquire(IPAddress.Loopback, out _));
    }

    [Fact]
    public void GlobalCap_RejectsOverLimit_ReleasesSlot()
    {
        var gate = new ConnectionGate(Caps(total: 2));
        Assert.True(gate.TryAcquire(IPAddress.Parse("1.1.1.1"), out var a));
        Assert.True(gate.TryAcquire(IPAddress.Parse("2.2.2.2"), out _));
        Assert.False(gate.TryAcquire(IPAddress.Parse("3.3.3.3"), out _));
        a.Dispose();
        Assert.True(gate.TryAcquire(IPAddress.Parse("3.3.3.3"), out _));
    }

    [Fact]
    public void PerIpCap_IsolatesPeers()
    {
        var gate = new ConnectionGate(Caps(perIp: 1));
        var ip = IPAddress.Parse("10.0.0.1");
        Assert.True(gate.TryAcquire(ip, out var lease));
        Assert.False(gate.TryAcquire(ip, out _));
        Assert.True(gate.TryAcquire(IPAddress.Parse("10.0.0.2"), out _));
        lease.Dispose();
        Assert.True(gate.TryAcquire(ip, out _));
    }

    [Fact]
    public void DenyList_Blocks_AllowListWins()
    {
        var deny = new ConnectionGate(Caps(deny: new[] { "9.9.9.9" }));
        Assert.True(deny.IsBlocked(IPAddress.Parse("9.9.9.9")));
        Assert.False(deny.IsBlocked(IPAddress.Parse("1.2.3.4")));

        var allow = new ConnectionGate(Caps(allow: new[] { "1.2.3.4" }, deny: new[] { "1.2.3.4" }));
        Assert.False(allow.IsBlocked(IPAddress.Parse("1.2.3.4"))); // allow precedence
        Assert.True(allow.IsBlocked(IPAddress.Parse("5.6.7.8")));  // not allow-listed
    }

    [Fact]
    public void DoubleDispose_ReleasesOnce()
    {
        var gate = new ConnectionGate(Caps(total: 1));
        Assert.True(gate.TryAcquire(IPAddress.Loopback, out var lease));
        lease.Dispose();
        lease.Dispose();
        Assert.True(gate.TryAcquire(IPAddress.Loopback, out _));
        Assert.False(gate.TryAcquire(IPAddress.Loopback, out _));
    }
}
