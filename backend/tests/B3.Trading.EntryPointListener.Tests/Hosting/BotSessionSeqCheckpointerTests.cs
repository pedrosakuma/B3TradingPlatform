using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// Sub-issue #172 (F). Behavioural tests for <see cref="BotSessionSeqCheckpointer.Tick"/>.
/// </summary>
public class BotSessionSeqCheckpointerTests
{
    [Fact]
    public async Task Tick_DispatchesEventForCredentialsWithCounter_AndResetsCounter()
    {
        var sessions = new InMemoryUserBotSessionRegistry();
        var cred = Guid.NewGuid();
        await sessions.GetOrCreateAsync(cred, default);

        var coord = new BotOutboundCoordinator(sessions, new BotErMultiplexerOptions());
        coord.AllocateNext(cred);
        coord.AllocateNext(cred);
        coord.AllocateNext(cred);
        coord.RecordOutbound(cred);
        coord.RecordOutbound(cred);
        coord.RecordOutbound(cred);

        var cp = new BotSessionSeqCheckpointer(coord, sessions,
            Options.Create(new BotErMultiplexerOptions()),
            NullLogger<BotSessionSeqCheckpointer>.Instance);

        cp.Tick();

        var state = await sessions.GetOrCreateAsync(cred, default);
        Assert.Equal(3ul, state.LastCheckpointedOutboundSeq);
        Assert.Equal(0, coord.GetCounter(cred));
    }

    [Fact]
    public async Task TickThresholdOnly_OnlyEmitsForCredentialsAtOrAboveThreshold()
    {
        var sessions = new InMemoryUserBotSessionRegistry();
        var credLow = Guid.NewGuid();
        var credHigh = Guid.NewGuid();
        await sessions.GetOrCreateAsync(credLow, default);
        await sessions.GetOrCreateAsync(credHigh, default);

        var coord = new BotOutboundCoordinator(sessions, new BotErMultiplexerOptions());
        coord.AllocateNext(credLow); coord.RecordOutbound(credLow);
        for (var i = 0; i < 5; i++) { coord.AllocateNext(credHigh); coord.RecordOutbound(credHigh); }

        var cp = new BotSessionSeqCheckpointer(coord, sessions,
            Options.Create(new BotErMultiplexerOptions { CheckpointMessageThreshold = 5 }),
            NullLogger<BotSessionSeqCheckpointer>.Instance);

        cp.TickThresholdOnly();

        Assert.Equal(0ul, (await sessions.GetOrCreateAsync(credLow, default)).LastCheckpointedOutboundSeq);
        Assert.Equal(5ul, (await sessions.GetOrCreateAsync(credHigh, default)).LastCheckpointedOutboundSeq);
        Assert.Equal(1, coord.GetCounter(credLow));
        Assert.Equal(0, coord.GetCounter(credHigh));
    }

    [Fact]
    public async Task Tick_DoesNotEmit_WhenCounterIsZero()
    {
        var sessions = new InMemoryUserBotSessionRegistry();
        var cred = Guid.NewGuid();
        await sessions.GetOrCreateAsync(cred, default);

        var coord = new BotOutboundCoordinator(sessions, new BotErMultiplexerOptions());
        _ = coord.GetOrCreateBuffer(cred);

        var cp = new BotSessionSeqCheckpointer(coord, sessions,
            Options.Create(new BotErMultiplexerOptions()),
            NullLogger<BotSessionSeqCheckpointer>.Instance);

        cp.Tick();

        var state = await sessions.GetOrCreateAsync(cred, default);
        Assert.Equal(0ul, state.LastCheckpointedOutboundSeq);
    }
}
