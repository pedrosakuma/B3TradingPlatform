using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// Sub-issue #172 (F). Behavioural tests for <see cref="BotErMultiplexer"/>:
/// routes by mapping → buffers + sends, drops unmapped, falls back to
/// buffer-only when bot is offline, and triggers BumpVersion+force-close
/// on overflow.
/// </summary>
public class BotErMultiplexerTests
{
    private static ExecutionEvent NewEvent(ulong clOrdId, ExecKind kind = ExecKind.New) =>
        new(
            Owner: new EndClientId("u1"),
            ClOrdId: clOrdId,
            Symbol: "PETR4",
            Side: OrderSide.Buy,
            Status: OrderStatus.Working,
            Kind: kind,
            LeavesQuantity: 100,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            TimestampUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void Directory_StaleTerminationCannotCloseReplacement()
    {
        var credentialId = Guid.NewGuid();
        var directory = new BotSessionConnectionDirectory();
        var oldSender = new RejectingSender();
        var replacement = new RejectingSender();
        directory.Register(credentialId, "old", oldSender);
        directory.Register(credentialId, "replacement", replacement);

        Assert.True(oldSender.Disposed);
        Assert.False(directory.TryForceTerminate(credentialId, "old"));
        Assert.False(replacement.Disposed);
        Assert.True(directory.TryGet(credentialId, out var current));
        Assert.Same(replacement, current);
    }

    [Fact]
    public async Task Route_MappedClOrdId_BuffersAndSends()
    {
        var (mux, ctx) = await NewMultiplexerAsync();
        ctx.Mappings.Add(internalId: 100, credId: ctx.CredentialId, externalId: 4242);
        ctx.Directory.Register(ctx.CredentialId, ctx.Sender);

        mux.Route(NewEvent(100));
        await ctx.AwaitDrainAsync();

        Assert.Equal(1, ctx.Sender.SentCount);
        Assert.Equal(1, ctx.Coordinator.GetOrCreateBuffer(ctx.CredentialId).Count);
        Assert.Equal(1, ctx.Coordinator.GetCounter(ctx.CredentialId));
    }

    [Fact]
    public async Task Route_UnmappedClOrdId_NoOps()
    {
        var (mux, ctx) = await NewMultiplexerAsync();
        // No mapping registered.

        mux.Route(NewEvent(404));
        await ctx.AwaitDrainAsync();

        Assert.Equal(0, ctx.Sender.SentCount);
    }

    [Fact]
    public async Task Route_BotOffline_BuffersWithoutSend()
    {
        var (mux, ctx) = await NewMultiplexerAsync();
        ctx.Mappings.Add(internalId: 100, credId: ctx.CredentialId, externalId: 4242);
        // Sender NOT registered with directory.

        mux.Route(NewEvent(100));
        await ctx.AwaitDrainAsync();

        Assert.Equal(0, ctx.Sender.SentCount);
        Assert.Equal(1, ctx.Coordinator.GetOrCreateBuffer(ctx.CredentialId).Count);
    }

    [Fact]
    public async Task Overflow_BumpsVersion_DisposesSender_AndResetsBuffer()
    {
        var (mux, ctx) = await NewMultiplexerAsync(bufferCap: 2);
        ctx.Mappings.Add(internalId: 100, credId: ctx.CredentialId, externalId: 4242);
        ctx.Directory.Register(ctx.CredentialId, ctx.Sender);
        var startVer = (await ctx.Sessions.GetOrCreateAsync(ctx.CredentialId, default)).CurrentVer;

        // Fill the buffer + one over the cap.
        mux.Route(NewEvent(100));
        mux.Route(NewEvent(100));
        mux.Route(NewEvent(100));
        await ctx.AwaitDrainAsync(rounds: 5);
        // Give overflow loop time to handle.
        for (var i = 0; i < 30 && !ctx.Sender.Disposed; i++)
            await Task.Delay(50);

        Assert.True(ctx.Sender.Disposed);
        var newState = await ctx.Sessions.GetOrCreateAsync(ctx.CredentialId, default);
        Assert.True(newState.CurrentVer > startVer);
        Assert.False(ctx.Coordinator.GetOrCreateBuffer(ctx.CredentialId).IsOverflowed);
    }

    [Fact]
    public async Task WriterOverflowBurst_IsCoalescedIntoOneBumpAndClose()
    {
        var credentialId = Guid.NewGuid();
        var sessions = new BlockingSessionRegistry(credentialId);
        var mappings = new FakeMappingRegistry();
        mappings.Add(100, credentialId, 4242);
        var directory = new BotSessionConnectionDirectory();
        var sender = new RejectingSender();
        directory.Register(credentialId, "conn-overflow", sender);
        var coordinator = new BotOutboundCoordinator(
            sessions,
            new BotErMultiplexerOptions { OutboundBufferMaxMessages = 1000 });
        var mux = new BotErMultiplexer(
            mappings, sessions, directory, coordinator,
            NullLogger<BotErMultiplexer>.Instance);
        await mux.StartAsync(CancellationToken.None);

        for (var i = 0; i < 20; i++)
            mux.Route(NewEvent(100));
        await sessions.BumpStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 20; i++)
            mux.Route(NewEvent(100));
        Assert.Equal(1, sessions.BumpCount);

        sessions.AllowBump.TrySetResult();
        for (var i = 0; i < 100 && !sender.Disposed; i++)
            await Task.Delay(10);

        Assert.True(sender.Disposed);
        Assert.Equal(1, sessions.BumpCount);
        Assert.Equal(1, sender.DisposeCount);
        await mux.StopAsync(CancellationToken.None);
    }

    private static async Task<(BotErMultiplexer, MuxContext)> NewMultiplexerAsync(int bufferCap = 1000)
    {
        var sessions = new InMemoryUserBotSessionRegistry();
        var credId = Guid.NewGuid();
        await sessions.GetOrCreateAsync(credId, default);

        var mappings = new FakeMappingRegistry();
        var directory = new BotSessionConnectionDirectory();
        var opts = Options.Create(new BotErMultiplexerOptions
        {
            OutboundBufferMaxMessages = bufferCap,
        });
        var coord = new BotOutboundCoordinator(sessions, opts.Value);
        var mux = new BotErMultiplexer(mappings, sessions, directory, coord,
            NullLogger<BotErMultiplexer>.Instance);
        // Start the BackgroundService manually so Tests don't need a host.
        var cts = new CancellationTokenSource();
        await mux.StartAsync(cts.Token);

        var sender = new FakeSender();
        var ctx = new MuxContext(credId, sessions, mappings, directory, coord, sender, mux, cts);
        return (mux, ctx);
    }

    private sealed class MuxContext
    {
        public Guid CredentialId { get; }
        public InMemoryUserBotSessionRegistry Sessions { get; }
        public FakeMappingRegistry Mappings { get; }
        public BotSessionConnectionDirectory Directory { get; }
        public BotOutboundCoordinator Coordinator { get; }
        public FakeSender Sender { get; }
        private readonly BotErMultiplexer _mux;
        private readonly CancellationTokenSource _cts;

        public MuxContext(
            Guid credId,
            InMemoryUserBotSessionRegistry sessions,
            FakeMappingRegistry mappings,
            BotSessionConnectionDirectory directory,
            BotOutboundCoordinator coordinator,
            FakeSender sender,
            BotErMultiplexer mux,
            CancellationTokenSource cts)
        {
            CredentialId = credId;
            Sessions = sessions;
            Mappings = mappings;
            Directory = directory;
            Coordinator = coordinator;
            Sender = sender;
            _mux = mux;
            _cts = cts;
        }

        public async Task AwaitDrainAsync(int rounds = 3)
        {
            // The route loop is single-threaded; a few yield-roundtrips
            // give the channel time to drain in the background.
            for (var i = 0; i < rounds; i++) await Task.Delay(20);
        }
    }

    private sealed class FakeSender : IBotSessionOutboundSender, IDisposable
    {
        public int SentCount;
        public bool Disposed;
        public bool TryEnqueue(OutboundFrame frame)
        {
            if (Disposed) return false;
            Interlocked.Increment(ref SentCount);
            return true;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class RejectingSender : IBotSessionOutboundSender, IDisposable
    {
        public int DisposeCount;
        public bool Disposed => Volatile.Read(ref DisposeCount) > 0;
        public bool TryEnqueue(OutboundFrame frame) => false;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class BlockingSessionRegistry : IUserBotSessionRegistry
    {
        private readonly BotSessionState _state;
        public int BumpCount;
        public TaskCompletionSource BumpStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowBump { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingSessionRegistry(Guid credentialId)
        {
            _state = new BotSessionState(credentialId, 1, 1, 0);
        }

        public Task<BotSessionState> GetOrCreateAsync(Guid credentialId, CancellationToken ct) =>
            Task.FromResult(_state);

        public Task<bool> TryClaimActiveAsync(
            Guid credentialId, ulong attemptedVer, string connectionId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task ReleaseAsync(Guid credentialId, string connectionId, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task<BotSessionVersionAdvance> BumpVersionAsync(
            Guid credentialId, string reason, CancellationToken ct)
        {
            Interlocked.Increment(ref BumpCount);
            BumpStarted.TrySetResult();
            await AllowBump.Task.WaitAsync(ct);
            return new BotSessionVersionAdvance(2, "conn-overflow");
        }

        public void UpdateCheckpointedOutboundSeq(Guid credentialId, ulong checkpointedSeq) { }
    }

    private sealed class FakeMappingRegistry : IUserBotOrderMappingRegistry
    {
        private readonly Dictionary<ulong, OrderMapping> _orders = new();

        public void Add(ulong internalId, Guid credId, ulong externalId)
            => _orders[internalId] = new OrderMapping(credId, externalId);

        public bool TryGetOrderMapping(ulong internalClOrdId, out OrderMapping mapping)
            => _orders.TryGetValue(internalClOrdId, out mapping);

        public bool TryGetByExternal(Guid credentialId, ulong externalClOrdId, out ulong internalClOrdId)
        { internalClOrdId = 0; return false; }
        public bool TryGetCancelMapping(ulong cancelInternalClOrdId, out CancelMapping mapping)
        { mapping = default; return false; }
        public void Reap(ulong internalClOrdId) { }
        public void ReapCancel(ulong cancelInternalClOrdId) { }
        public void RegisterOrderInternal(ulong internalClOrdId, Guid credentialId, ulong externalClOrdId) { }
        public void RegisterCancelInternal(ulong c, ulong o, Guid g, ulong e) { }
        public IReadOnlyList<BotOrderMappingSnapshot> SnapshotOrders() => Array.Empty<BotOrderMappingSnapshot>();
        public IReadOnlyList<BotCancelMappingSnapshot> SnapshotCancels() => Array.Empty<BotCancelMappingSnapshot>();
        public BotOrderMappingRaw[] RawSnapshotOrders() => Array.Empty<BotOrderMappingRaw>();
        public BotCancelMappingRaw[] RawSnapshotCancels() => Array.Empty<BotCancelMappingRaw>();
        public void Restore(IEnumerable<BotOrderMappingSnapshot> orders, IEnumerable<BotCancelMappingSnapshot> cancels) { }
    }
}
