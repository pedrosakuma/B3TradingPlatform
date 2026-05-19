using System.Net;
using System.Net.Sockets;

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
/// Harness v2 (#228) live-socket variant of <see cref="BotErRouter_RouteOne_Bench"/>.
/// The original bench used an in-process <c>CountingSender</c> stub, which means
/// PRs that touched the per-connection writer Task (P8 / #202) and the
/// <see cref="Socket.NoDelay"/> flag (P11) could not measure the change
/// against a baseline that actually went through the production wire path.
///
/// <para>This bench keeps everything from the in-process variant (router,
/// coordinator, per-credential <see cref="BotOutboundBuffer"/>, ownership
/// rules from P7 / PR #218) and only swaps the sender: each
/// <see cref="MappedCredentials"/> credential gets its own ephemeral TCP
/// loopback connection (<see cref="TcpListener"/> bound to
/// <c>IPAddress.Loopback:0</c>) and a real
/// <see cref="FixpOutboundChannelWriter"/> drain loop (one Task per
/// connection — the P8 invariant) writing into the connected socket with
/// <see cref="Socket.NoDelay"/> enabled (P11).</para>
///
/// <para><b>Receive side.</b> Each accepted server-side socket is drained
/// into a discard buffer by a per-connection background read loop so the
/// kernel send buffer never back-pressures the writer (which would turn
/// the bench into a measure of TCP buffer sizing rather than the writer
/// loop). Bytes received are not parsed — the bench just needs to ensure
/// the writer's <c>WriteAsync</c> consistently completes.</para>
///
/// <para><b>Completion signal.</b> Each iteration counts frames the drain
/// callback has actually completed writing to the wire, NOT
/// <c>TryEnqueue</c> returns. <see cref="RouteBatch"/> spin-waits until
/// that counter reaches <see cref="BatchSize"/>, with a 30 s deadline so a
/// regression that stalls the writer surfaces as a loud
/// <see cref="TimeoutException"/> instead of a hung benchmark process.
/// BenchmarkDotNet handles statistics — the writer's iteration count and
/// warmup are kept on BDN defaults so the spin-wait never gates the
/// statistical confidence intervals.</para>
///
/// <para><b>Lifecycle.</b> <see cref="GlobalSetup"/> binds the listener,
/// connects every client, sets <see cref="Socket.NoDelay"/>, starts each
/// per-connection drain loop and the receive-side discard loop.
/// <see cref="GlobalCleanup"/> disposes every writer (which drains then
/// closes the socket), tears down the receive loops, and disposes the
/// listener. No port is held past <see cref="GlobalCleanup"/> — the OS
/// reclaims the ephemeral port as soon as the listener and the
/// connected sockets are closed.</para>
///
/// <para><b>Single-machine loopback caveat.</b> This is still a single-host
/// loopback — kernel TCP fast-path, no NIC / wire latency. Cross-host
/// numbers are #207 / load-test territory; see the bench README "Known
/// limitations" section.</para>
/// </summary>
[MemoryDiagnoser]
public class BotErRouter_RouteOne_LiveSocket_Bench
{
    [Params(64, 1024)]
    public int BatchSize { get; set; }

    [Params(1, 16)]
    public int MappedCredentials { get; set; }

    private BotErMultiplexer _mux = null!;
    private CancellationTokenSource _cts = null!;
    private FakeMappingRegistry _mappings = null!;
    private BotOutboundCoordinator _coord = null!;

    private TcpListener _listener = null!;
    private CancellationTokenSource _acceptCts = null!;
    private LiveSocketSender[] _senders = null!;
    private TcpClient[] _serverSides = null!;
    private Task[] _serverDrainTasks = null!;
    private Guid[] _credIds = null!;
    private ulong[] _internalIds = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;

        var sessions = new InMemoryUserBotSessionRegistry();
        var directory = new BotSessionConnectionDirectory();
        _mappings = new FakeMappingRegistry();

        _credIds = new Guid[MappedCredentials];
        _senders = new LiveSocketSender[MappedCredentials];
        _serverSides = new TcpClient[MappedCredentials];
        _serverDrainTasks = new Task[MappedCredentials];
        _internalIds = new ulong[BatchSize];
        _acceptCts = new CancellationTokenSource();

        for (var i = 0; i < MappedCredentials; i++)
        {
            _credIds[i] = Guid.NewGuid();
            await sessions.GetOrCreateAsync(_credIds[i], default).ConfigureAwait(false);

            // Connect client + accept server in parallel; await both.
            var client = new TcpClient();
            var connectTask = client.ConnectAsync(endpoint.Address, endpoint.Port);
            var acceptTask = _listener.AcceptTcpClientAsync(_acceptCts.Token).AsTask();
            await Task.WhenAll(connectTask, acceptTask).ConfigureAwait(false);

            client.NoDelay = true;            // P11
            var server = acceptTask.Result;
            server.NoDelay = true;            // symmetric, mirrors prod tuning
            _serverSides[i] = server;

            // Per-connection server-side discard loop. Without this the
            // writer would block once kernel send buffer fills.
            _serverDrainTasks[i] = Task.Run(() => DiscardLoopAsync(server, _acceptCts.Token));

            var sender = new LiveSocketSender(
                client,
                channelCapacity: Math.Max(8192, BatchSize * 4),
                connectionId: $"bench-{i}");
            _senders[i] = sender;
            directory.Register(_credIds[i], sender);
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
            OutboundBufferMaxMessages = Math.Max(8192, BatchSize * 4),
        });
        _coord = new BotOutboundCoordinator(sessions, opts.Value);
        _mux = new BotErMultiplexer(_mappings, sessions, directory, _coord,
            NullLogger<BotErMultiplexer>.Instance);

        _cts = new CancellationTokenSource();
        await _mux.StartAsync(_cts.Token).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try { await _mux.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Stop accepting new connections + cancel discard loops.
        try { _acceptCts.Cancel(); } catch { /* ignore */ }

        if (_senders is not null)
        {
            foreach (var s in _senders)
            {
                try { await s.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }

        if (_serverSides is not null)
        {
            foreach (var s in _serverSides)
            {
                try { s.Close(); } catch { /* ignore */ }
            }
        }

        if (_serverDrainTasks is not null)
        {
            try { await Task.WhenAll(_serverDrainTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* best-effort — discard loops exit on socket close */ }
        }

        try { _listener.Stop(); } catch { /* ignore */ }
        _acceptCts.Dispose();
        _cts.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Mirror the in-process bench: reset per-iteration counters AND
        // the per-credential outbound buffers. Without the buffer reset
        // BotOutboundBuffer would permanently mark itself overflowed
        // mid-run and silently make Route skip TryEnqueue, hanging the
        // spin-wait in RouteBatch.
        foreach (var s in _senders) s.ResetCounter();
        foreach (var credId in _credIds) _coord.GetOrCreateBuffer(credId).Reset();
    }

    [Benchmark]
    public void RouteBatch()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            _mux.Route(NewEvent(_internalIds[i]));
        }

        var deadline = Environment.TickCount64 + 30_000;
        var spin = new SpinWait();
        while (TotalDrained() < BatchSize)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"RouteBatch live-socket drain stalled at {TotalDrained()}/{BatchSize} after 30s");
            }
            spin.SpinOnce();
        }
    }

    private int TotalDrained()
    {
        var sum = 0;
        foreach (var s in _senders) sum += s.DrainedCount;
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

    private static async Task DiscardLoopAsync(TcpClient server, CancellationToken ct)
    {
        var buf = new byte[8 * 1024];
        try
        {
            var stream = server.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) return; // peer closed
            }
        }
        catch { /* socket closed / cancelled — exit */ }
    }

    /// <summary>
    /// Thin <see cref="IBotSessionOutboundSender"/> that owns one TCP
    /// connection and one <see cref="FixpOutboundChannelWriter"/> drain
    /// loop, mirroring the production
    /// <c>FixpSessionConnection.IBotSessionOutboundSender.TryEnqueue</c>
    /// path. Counts frames the drain loop has actually written to the
    /// wire so the bench can wait on real throughput, not enqueue
    /// success.
    /// </summary>
    private sealed class LiveSocketSender : IBotSessionOutboundSender, IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly FixpOutboundChannelWriter _writer;
        private int _drained;

        public LiveSocketSender(TcpClient client, int channelCapacity, string connectionId)
        {
            _client = client;
            _stream = client.GetStream();
            _writer = new FixpOutboundChannelWriter(
                capacity: channelCapacity,
                writeAsync: WriteAsync,
                connectionId: connectionId,
                logger: null);
        }

        public int DrainedCount => Volatile.Read(ref _drained);
        public void ResetCounter() => Volatile.Write(ref _drained, 0);

        public bool TryEnqueue(OutboundFrame frame) => _writer.TryEnqueue(frame);

        private async ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            try
            {
                await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _drained);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _writer.CompleteAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* best-effort */ }
            try { _stream.Close(); } catch { /* ignore */ }
            try { _client.Close(); } catch { /* ignore */ }
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
