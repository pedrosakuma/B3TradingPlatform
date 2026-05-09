using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// Sub-issue #172 (F). Unit tests for <see cref="BotOutboundCoordinator"/>.
/// </summary>
public class BotOutboundCoordinatorTests
{
    private static BotOutboundCoordinator NewCoord(IUserBotSessionRegistry? sessions = null, int cap = 1000)
        => new(sessions ?? new InMemoryUserBotSessionRegistry(),
               new BotErMultiplexerOptions { OutboundBufferMaxMessages = cap });

    [Fact]
    public void AllocateNext_IsPerCredential_AndStartsAtOne()
    {
        var c = NewCoord();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(1ul, c.AllocateNext(a));
        Assert.Equal(2ul, c.AllocateNext(a));
        Assert.Equal(1ul, c.AllocateNext(b));
        Assert.Equal(2ul, c.GetCurrentSeq(a));
    }

    [Fact]
    public void RecordOutbound_BumpsCounter_ReadAndResetReturnsPrior()
    {
        var c = NewCoord();
        var cred = Guid.NewGuid();
        c.RecordOutbound(cred);
        c.RecordOutbound(cred);
        c.RecordOutbound(cred);
        Assert.Equal(3, c.GetCounter(cred));
        Assert.Equal(3, c.ReadAndResetCounter(cred));
        Assert.Equal(0, c.GetCounter(cred));
    }

    [Fact]
    public void ListActiveCredentials_OnlyReturnsThoseWithNonZeroCounter()
    {
        var c = NewCoord();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        c.RecordOutbound(a);
        _ = c.GetOrCreateBuffer(b);
        var active = c.ListActiveCredentials();
        Assert.Contains(a, active);
        Assert.DoesNotContain(b, active);
    }

    [Fact]
    public void OnBufferOverflow_FiresThroughCoordinator()
    {
        var c = NewCoord(cap: 2);
        var caught = new List<Guid>();
        c.OnBufferOverflow = caught.Add;
        var cred = Guid.NewGuid();
        var buf = c.GetOrCreateBuffer(cred);
        Assert.True(buf.Append(1, new byte[] { 1 }));
        Assert.True(buf.Append(2, new byte[] { 2 }));
        Assert.False(buf.Append(3, new byte[] { 3 })); // overflow
        Assert.Single(caught);
        Assert.Equal(cred, caught[0]);
    }

    [Fact]
    public async Task AllocateNext_SeedsFromCheckpointedSeq()
    {
        var sessions = new InMemoryUserBotSessionRegistry();
        var cred = Guid.NewGuid();
        await sessions.GetOrCreateAsync(cred, default);
        sessions.UpdateCheckpointedOutboundSeq(cred, 100);

        var c = NewCoord(sessions);
        Assert.Equal(101ul, c.AllocateNext(cred));
    }
}
