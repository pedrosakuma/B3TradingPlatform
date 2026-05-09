using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

public class MaxSessionsPerUserTests
{
    [Fact]
    public void TryIncrement_AllowsUpToMax()
    {
        var counter = new UserSessionCounter();
        Assert.True(counter.TryIncrement("user1", 3));
        Assert.True(counter.TryIncrement("user1", 3));
        Assert.True(counter.TryIncrement("user1", 3));
        Assert.False(counter.TryIncrement("user1", 3));
    }

    [Fact]
    public void TryIncrement_DifferentUsers_Independent()
    {
        var counter = new UserSessionCounter();
        Assert.True(counter.TryIncrement("user1", 1));
        Assert.False(counter.TryIncrement("user1", 1));
        Assert.True(counter.TryIncrement("user2", 1));
    }

    [Fact]
    public void Decrement_FreesSlot()
    {
        var counter = new UserSessionCounter();
        Assert.True(counter.TryIncrement("user1", 1));
        Assert.False(counter.TryIncrement("user1", 1));
        counter.Decrement("user1");
        Assert.True(counter.TryIncrement("user1", 1));
    }

    [Fact]
    public void TotalActiveSessions_SumsAcrossUsers()
    {
        var counter = new UserSessionCounter();
        counter.TryIncrement("user1", 10);
        counter.TryIncrement("user1", 10);
        counter.TryIncrement("user2", 10);
        Assert.Equal(3, counter.TotalActiveSessions);
    }
}
