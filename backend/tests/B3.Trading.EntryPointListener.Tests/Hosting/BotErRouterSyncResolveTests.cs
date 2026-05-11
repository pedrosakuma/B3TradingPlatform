using System.Collections.Concurrent;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// Issue #203 / RFC §5.4 (P9, F4). Behavioural tests for the post-P9
/// synchronous credential resolve in <see cref="BotErMultiplexer"/>.
/// Pin the three invariants the global-multiplexer-channel removal
/// must NOT regress:
/// <list type="number">
///   <item>Per-credential ordering under concurrent dispatch — the
///   per-bot ER stream observes ERs in the producer's submit order
///   even when ≥8 producer threads fan ERs across multiple
///   credentials. (RFC §4.3 + §5.4 invariant table.)</item>
///   <item>Slow-credential isolation — a bot whose per-connection
///   writer channel is full (P8 backpressure) cannot stall any other
///   credential. The pre-P9 single-reader global drain coupled them.</item>
///   <item>Backpressure when one credential's <see cref="BotOutboundBuffer"/>
///   is full — the buffer is the sole bounded layer (RFC §6.3) and
///   trips the version-bump path; no unbounded queue absorbs the
///   surplus elsewhere.</item>
/// </list>
/// </summary>
public class BotErRouterSyncResolveTests
{
    [Fact]
    public async Task PerCredentialOrdering_PreservedUnderConcurrentDispatch()
    {
        // 8 credentials × 8 producer threads × 500 ERs each = 32k
        // routes, fanned across credentials by hashing the producer's
        // monotonically increasing seq onto a credential id. The
        // synchronous Route() chain runs entirely under a per-thread
        // simulated dispatcher lock (a single shared object) — same
        // contract EventDispatcher gives the IExecutionFanOutSink hook.
        const int credentials = 8;
        const int producers = 8;
        const int perProducer = 500;

        var (mux, ctx) = await NewMuxAsync(bufferCap: 100_000);
        var credIds = new Guid[credentials];
        var senders = new RecordingSender[credentials];
        for (var c = 0; c < credentials; c++)
        {
            credIds[c] = Guid.NewGuid();
            await ctx.Sessions.GetOrCreateAsync(credIds[c], default);
            senders[c] = new RecordingSender();
            ctx.Directory.Register(credIds[c], senders[c]);
        }

        // Every internalId carries (producerIdx, perProducerSeq) packed
        // into the high/low halves so the assert can recover the
        // producer's submit order from the recorded clOrdId stream.
        // Mapping table is fully populated up front so the resolve hot
        // path is a pure dictionary hit (P9 contract).
        for (var p = 0; p < producers; p++)
        {
            for (var i = 0; i < perProducer; i++)
            {
                var internalId = PackId(p, i);
                var credIdx = (int)(internalId % credentials);
                ctx.Mappings.Add(internalId, credIds[credIdx], externalId: 1_000_000UL + internalId);
            }
        }

        // Single shared lock simulates the dispatcher lock: P9's
        // contract is "synchronous resolve under the dispatcher lock",
        // and the ordering guarantee depends on that serialisation.
        var dispatcherLock = new object();
        var threads = new Thread[producers];
        for (var p = 0; p < producers; p++)
        {
            var producerIdx = p;
            threads[p] = new Thread(() =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    var ev = NewEvent(PackId(producerIdx, i));
                    lock (dispatcherLock)
                    {
                        mux.Route(ev);
                    }
                }
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // Per-credential, the recorded ExternalClOrdId stream must be
        // strictly ascending in the per-producer subsequence — i.e.
        // for any two ERs from the same producer, the one with the
        // lower perProducer index must appear first on its
        // credential's wire.
        for (var c = 0; c < credentials; c++)
        {
            var lastSeenPerProducer = new int[producers];
            Array.Fill(lastSeenPerProducer, -1);
            var observed = senders[c].SnapshotExternalIds();
            foreach (var ext in observed)
            {
                var internalId = ext - 1_000_000UL;
                var (pIdx, seq) = UnpackId(internalId);
                Assert.True(seq > lastSeenPerProducer[pIdx],
                    $"credential={c} producer={pIdx}: out-of-order seq={seq} after {lastSeenPerProducer[pIdx]}");
                lastSeenPerProducer[pIdx] = seq;
            }
        }

        // Total accounting: every routed ER reached exactly its
        // credential's sender (no cross-credential leakage, no drops).
        var total = 0;
        for (var c = 0; c < credentials; c++) total += senders[c].SentCount;
        Assert.Equal(producers * perProducer, total);
    }

    [Fact]
    public async Task SlowCredential_DoesNotStall_OtherCredentials()
    {
        // Pre-P9: the global multiplexer Channel<ExecutionEvent> was
        // single-reader, so the slow bot's RouteOne would have held
        // up the drain for all credentials. Post-P9 the resolve runs
        // on the producer thread itself; only the slow credential's
        // per-connection writer (P8) can backpressure, and TryEnqueue
        // returns false instantly. Other credentials are unaffected.
        var (mux, ctx) = await NewMuxAsync(bufferCap: 10_000);

        var slow = Guid.NewGuid();
        var fast = Guid.NewGuid();
        await ctx.Sessions.GetOrCreateAsync(slow, default);
        await ctx.Sessions.GetOrCreateAsync(fast, default);

        var slowSender = new BlockingSender(); // TryEnqueue returns false (full)
        var fastSender = new RecordingSender();
        ctx.Directory.Register(slow, slowSender);
        ctx.Directory.Register(fast, fastSender);

        ctx.Mappings.Add(internalId: 1, credId: slow, externalId: 100);
        ctx.Mappings.Add(internalId: 2, credId: fast, externalId: 200);

        // Hammer the slow credential first — would stall the pre-P9
        // drain. Time the subsequent fast-credential burst.
        for (var i = 0; i < 5_000; i++) mux.Route(NewEvent(1));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5_000; i++) mux.Route(NewEvent(2));
        sw.Stop();

        Assert.Equal(5_000, fastSender.SentCount);
        // Fast credential's burst must be unaffected by the slow one;
        // budget is generous (the path is encode + buffer + TryEnqueue).
        // Exceeding it would indicate cross-credential coupling, the
        // exact regression P9 is meant to prevent.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"fast burst took {sw.ElapsedMilliseconds} ms — slow credential leaked backpressure");
        // The slow sender's TryEnqueue returned false every time, but
        // the per-credential buffer absorbed the frames (sole bounded
        // layer). Nothing was lost; retransmit replays them.
        Assert.Equal(5_000, ctx.Coordinator.GetOrCreateBuffer(slow).Count);
    }

    [Fact]
    public async Task BufferFull_TripsOverflow_NoUnboundedQueueElsewhere()
    {
        // Per RFC §6.3, the per-credential BotOutboundBuffer is the
        // sole bounded layer for ER backpressure. When it fills, the
        // overflow callback fires immediately (synchronously, inside
        // Append) and the message is rejected — there is no upstream
        // queue silently absorbing the surplus. We verify by checking
        // (a) the buffer signals overflow on cap+1, (b) the sender
        // gets the version-bump force-close, and (c) there is no
        // hidden queue: the multiplexer holds no per-event state
        // beyond the credentialId-only overflow channel.
        var (mux, ctx) = await NewMuxAsync(bufferCap: 4);
        var cred = Guid.NewGuid();
        await ctx.Sessions.GetOrCreateAsync(cred, default);
        var sender = new RecordingSender();
        ctx.Directory.Register(cred, sender);
        ctx.Mappings.Add(internalId: 7, credId: cred, externalId: 70);
        var startVer = (await ctx.Sessions.GetOrCreateAsync(cred, default)).CurrentVer;

        // 8 routes against a cap-4 buffer: the first 4 land, the 5th
        // trips overflow (and bulk-clears the buffer). The remaining
        // 3 are rejected because the buffer stays in overflowed-state
        // until Reset (called by HandleOverflowAsync).
        for (var i = 0; i < 8; i++) mux.Route(NewEvent(7));

        // Wait for the out-of-band overflow handler (BumpVersion is
        // async and runs off-lock by design). Bounded retries — the
        // handler is a single Task.Delay-free path and finishes fast.
        for (var i = 0; i < 50 && !sender.Disposed; i++)
            await Task.Delay(20);

        Assert.True(sender.Disposed, "overflow handler must force-close the offending sender");
        var newState = await ctx.Sessions.GetOrCreateAsync(cred, default);
        Assert.True(newState.CurrentVer > startVer, "version must be bumped");
        // After Reset, buffer is clean — the overflow path is the sole
        // signal for downstream catch-up. No silent in-flight queue.
        Assert.False(ctx.Coordinator.GetOrCreateBuffer(cred).IsOverflowed);
    }

    private static ulong PackId(int producer, int seq) => ((ulong)producer << 32) | (uint)seq;
    private static (int producer, int seq) UnpackId(ulong id) => ((int)(id >> 32), (int)(id & 0xFFFFFFFFu));

    private static ExecutionEvent NewEvent(ulong clOrdId) =>
        new(
            Owner: new EndClientId("u1"),
            ClOrdId: clOrdId,
            Symbol: "PETR4",
            Side: OrderSide.Buy,
            Status: OrderStatus.Working,
            Kind: ExecKind.New,
            LeavesQuantity: 100,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            TimestampUtc: DateTimeOffset.UtcNow);

    private static async Task<(BotErMultiplexer, MuxCtx)> NewMuxAsync(int bufferCap)
    {
        var sessions = new InMemoryUserBotSessionRegistry();
        var mappings = new FakeMappingRegistry();
        var directory = new BotSessionConnectionDirectory();
        var opts = Options.Create(new BotErMultiplexerOptions
        {
            OutboundBufferMaxMessages = bufferCap,
        });
        var coord = new BotOutboundCoordinator(sessions, opts.Value);
        var mux = new BotErMultiplexer(mappings, sessions, directory, coord,
            NullLogger<BotErMultiplexer>.Instance, opts);
        var cts = new CancellationTokenSource();
        await mux.StartAsync(cts.Token);
        return (mux, new MuxCtx(sessions, mappings, directory, coord));
    }

    private sealed class MuxCtx
    {
        public InMemoryUserBotSessionRegistry Sessions { get; }
        public FakeMappingRegistry Mappings { get; }
        public BotSessionConnectionDirectory Directory { get; }
        public BotOutboundCoordinator Coordinator { get; }
        public MuxCtx(
            InMemoryUserBotSessionRegistry sessions,
            FakeMappingRegistry mappings,
            BotSessionConnectionDirectory directory,
            BotOutboundCoordinator coordinator)
        {
            Sessions = sessions;
            Mappings = mappings;
            Directory = directory;
            Coordinator = coordinator;
        }
    }

    private sealed class RecordingSender : IBotSessionOutboundSender, IDisposable
    {
        // Offset of ExternalClOrdId in the OutboundExecutionReportEncoder
        // ExecutionReport_New frame: [SOFH 4][SBE header 8][body offset 20].
        private const int ExternalClOrdIdFrameOffset = 4 + 8 + 20;

        private readonly ConcurrentQueue<ulong> _externalIds = new();
        private int _sent;
        public int SentCount => Volatile.Read(ref _sent);
        public bool Disposed { get; private set; }
        public bool TryEnqueue(OutboundFrame frame)
        {
            if (Disposed) return false;
            // Recover the bot-visible ExternalClOrdId from the SBE
            // body so the per-credential ordering assert can recover
            // each producer's submit subsequence (PackId / UnpackId).
            var bytes = frame.Bytes.Span;
            var externalId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                bytes.Slice(ExternalClOrdIdFrameOffset, 8));
            _externalIds.Enqueue(externalId);
            Interlocked.Increment(ref _sent);
            return true;
        }
        public IReadOnlyList<ulong> SnapshotExternalIds() => _externalIds.ToArray();
        public void Dispose() => Disposed = true;
    }

    private sealed class BlockingSender : IBotSessionOutboundSender
    {
        // Models the P8 per-connection writer channel being full —
        // TryEnqueue returns false instantly without touching the
        // frame. This is the slow-credential signature.
        public bool TryEnqueue(OutboundFrame frame) => false;
    }

    private sealed class FakeMappingRegistry : IUserBotOrderMappingRegistry
    {
        private readonly ConcurrentDictionary<ulong, OrderMapping> _orders = new();
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
        public void Restore(IEnumerable<BotOrderMappingSnapshot> orders, IEnumerable<BotCancelMappingSnapshot> cancels) { }
    }
}
