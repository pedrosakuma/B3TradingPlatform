using BenchmarkDotNet.Attributes;

using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Hosting;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Benchmarks.Benches;

/// <summary>
/// RFC §7.1 — baseline for the bot ER router that F4/F5 target.
/// Measures end-to-end <see cref="BotErMultiplexer.Route"/> + drain +
/// <see cref="OutboundExecutionReportEncoder.Encode"/> + outbound
/// enqueue. Each invocation routes <see cref="BatchSize"/> events for
/// <see cref="MappedCredentials"/> rotating credentials and waits until
/// the in-process fake sender has observed all of them.
///
/// <para>The router is a <c>BackgroundService</c>; we start it in
/// <see cref="GlobalSetup"/> and reset per-iteration counters in
/// <see cref="IterationSetup"/> so each benchmark sample is independent.
/// </para>
///
/// <para>Acceptance gates (RFC §7.3): F4 — no throughput regression vs.
/// F2 baseline; F5 — outbound bytes alloc −95%. P7's PR records
/// before/after numbers in its body referencing this bench by name.</para>
/// </summary>
[MemoryDiagnoser]
public class BotErRouter_RouteOne_Bench
{
    [Params(64, 1024)]
    public int BatchSize { get; set; }

    [Params(1, 16)]
    public int MappedCredentials { get; set; }

    private BotErMultiplexer _mux = null!;
    private CancellationTokenSource _cts = null!;
    private FakeMappingRegistry _mappings = null!;
    private BotOutboundCoordinator _coord = null!;
    private CountingSender[] _senders = null!;
    private Guid[] _credIds = null!;
    private ulong[] _internalIds = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var sessions = new InMemoryUserBotSessionRegistry();
        var directory = new BotSessionConnectionDirectory();
        _mappings = new FakeMappingRegistry();

        _credIds = new Guid[MappedCredentials];
        _senders = new CountingSender[MappedCredentials];
        _internalIds = new ulong[BatchSize];

        for (var i = 0; i < MappedCredentials; i++)
        {
            _credIds[i] = Guid.NewGuid();
            await sessions.GetOrCreateAsync(_credIds[i], default).ConfigureAwait(false);
            _senders[i] = new CountingSender();
            directory.Register(_credIds[i], _senders[i]);
        }

        for (var i = 0; i < BatchSize; i++)
        {
            var internalId = (ulong)(i + 1);
            _internalIds[i] = internalId;
            var cred = _credIds[i % MappedCredentials];
            _mappings.Add(internalId, cred, externalId: 1_000_000UL + internalId);
        }

        var opts = Options.Create(new BotErMultiplexerOptions
        {
            // Big enough to swallow the largest batch without overflow.
            OutboundBufferMaxMessages = Math.Max(8192, BatchSize * 4),
        });
        var coord = new BotOutboundCoordinator(sessions, opts.Value);
        _coord = coord;
        _mux = new BotErMultiplexer(_mappings, sessions, directory, coord,
            NullLogger<BotErMultiplexer>.Instance, opts);

        _cts = new CancellationTokenSource();
        await _mux.StartAsync(_cts.Token).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try { await _mux.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* best-effort */ }
        _cts.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Resetting both the sender counters AND the per-credential
        // outbound buffers is required: BotOutboundBuffer permanently
        // marks itself overflowed once full and would silently make
        // subsequent RouteOne calls skip TryEnqueue, hanging the
        // spin-wait below. Resetting here keeps each iteration
        // independent and isolated from the previous one's buffered
        // bytes (which are also released, keeping memory bounded).
        foreach (var s in _senders) s.Reset();
        foreach (var credId in _credIds) _coord.GetOrCreateBuffer(credId).Reset();
    }

    [Benchmark]
    public void RouteBatch()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            _mux.Route(NewEvent(_internalIds[i]));
        }

        // Spin-wait until the drain loop has observed every event.
        // Cap the wait at 30s so a regression that breaks routing
        // surfaces as a loud failure, not a hung benchmark process.
        var deadline = Environment.TickCount64 + 30_000;
        var spin = new SpinWait();
        while (TotalSent() < BatchSize)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"RouteBatch drain stalled at {TotalSent()}/{BatchSize} after 30s");
            }
            spin.SpinOnce();
        }
    }

    private int TotalSent()
    {
        var sum = 0;
        foreach (var s in _senders) sum += s.SentCount;
        return sum;
    }

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

    private sealed class CountingSender : IBotSessionOutboundSender
    {
        private int _sent;
        public int SentCount => Volatile.Read(ref _sent);
        public void Reset() => Volatile.Write(ref _sent, 0);
        public bool TryEnqueue(OutboundFrame frame)
        {
            Interlocked.Increment(ref _sent);
            return true;
        }
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
