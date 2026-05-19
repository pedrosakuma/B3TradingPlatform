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
