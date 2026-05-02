using B3.EntryPoint.Client.Fixp;
using B3.Trading.Infrastructure;

namespace B3.Trading.Application.Tests;

public class FixpStateGaugeProjectorTests
{
    [Fact]
    public void Project_EmitsExactlyOneRowAtOneAndAllOthersAtZero()
    {
        var rows = FixpStateGaugeProjector.Project(FixpClientState.Established).ToArray();

        Assert.Equal(9, rows.Length);
        Assert.Single(rows, r => r.Value == 1);
        Assert.Equal("established", rows.Single(r => r.Value == 1).Key);
    }

    [Fact]
    public void Project_DisconnectedDefault()
    {
        var rows = FixpStateGaugeProjector.Project(FixpClientState.Disconnected).ToArray();
        Assert.Equal("disconnected", rows.Single(r => r.Value == 1).Key);
    }

    [Theory]
    [InlineData(FixpClientState.Disconnected, "disconnected")]
    [InlineData(FixpClientState.TcpConnected, "tcp_connected")]
    [InlineData(FixpClientState.Negotiating, "negotiating")]
    [InlineData(FixpClientState.Negotiated, "negotiated")]
    [InlineData(FixpClientState.Establishing, "establishing")]
    [InlineData(FixpClientState.Established, "established")]
    [InlineData(FixpClientState.Suspended, "suspended")]
    [InlineData(FixpClientState.Terminating, "terminating")]
    [InlineData(FixpClientState.Terminated, "terminated")]
    public void Project_StateTagsMatch(FixpClientState state, string expectedTag)
    {
        var rows = FixpStateGaugeProjector.Project(state).ToArray();
        Assert.Equal(expectedTag, rows.Single(r => r.Value == 1).Key);
    }

    [Fact]
    public void Project_AllStatesCovered()
    {
        // Every value in the SDK enum must appear in the projection so a new
        // SDK release adding a state surfaces this test as a failure.
        var sdkStates = Enum.GetValues<FixpClientState>().ToHashSet();
        var projectedTags = FixpStateGaugeProjector.Project(FixpClientState.Disconnected)
            .Select(r => r.Key)
            .ToHashSet();
        Assert.Equal(sdkStates.Count, projectedTags.Count);
    }
}
