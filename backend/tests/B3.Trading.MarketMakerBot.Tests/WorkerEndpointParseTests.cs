using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class WorkerEndpointParseTests
{
    [Fact]
    public void ParseEndpoint_HostAndPort_Ok()
    {
        var ep = EndpointParser.Parse("matching-platform:9876");
        Assert.Equal("matching-platform", ep.Host);
        Assert.Equal(9876, ep.Port);
    }

    [Theory]
    [InlineData("matching-platform")]
    [InlineData("matching-platform:")]
    [InlineData(":9876")]
    [InlineData("matching-platform:0")]
    [InlineData("matching-platform:99999")]
    [InlineData("matching-platform:abc")]
    public void ParseEndpoint_Invalid_Throws(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => EndpointParser.Parse(endpoint));
    }
}
