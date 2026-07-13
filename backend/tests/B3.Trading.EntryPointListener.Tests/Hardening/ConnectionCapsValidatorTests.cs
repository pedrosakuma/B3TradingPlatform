namespace B3.Trading.EntryPointListener.Tests.Hardening;

/// <summary>
/// Covers the #529 public-hardening validation rules in
/// <see cref="EntryPointListenerOptionsValidator"/>.
/// </summary>
public sealed class ConnectionCapsValidatorTests
{
    private readonly EntryPointListenerOptionsValidator _validator = new();

    private static EntryPointListenerOptions Base() => new()
    {
        Enabled = true,
        Endpoint = "127.0.0.1:5001",
    };

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.True(_validator.Validate(null, Base()).Succeeded);
    }

    [Fact]
    public void NegativeCaps_Fail()
    {
        var o = Base();
        o.ConnectionCaps.MaxConcurrentTotal = -1;
        o.ConnectionCaps.MaxConcurrentPerIp = -1;
        Assert.True(_validator.Validate(null, o).Failed);
    }

    [Fact]
    public void NonPositiveHandshakeTimeout_Fails()
    {
        var o = Base();
        o.Tls.HandshakeTimeout = TimeSpan.Zero;
        Assert.True(_validator.Validate(null, o).Failed);
    }

    [Fact]
    public void MalformedIp_Fails()
    {
        var o = Base();
        o.ConnectionCaps.DeniedIps.Add("not-an-ip");
        Assert.True(_validator.Validate(null, o).Failed);
    }

    [Fact]
    public void ValidCapsAndIps_Succeed()
    {
        var o = Base();
        o.ConnectionCaps.MaxConcurrentTotal = 500;
        o.ConnectionCaps.MaxConcurrentPerIp = 5;
        o.ConnectionCaps.AllowedIps.Add("10.0.0.1");
        o.ConnectionCaps.DeniedIps.Add("9.9.9.9");
        Assert.True(_validator.Validate(null, o).Succeeded);
    }
}
