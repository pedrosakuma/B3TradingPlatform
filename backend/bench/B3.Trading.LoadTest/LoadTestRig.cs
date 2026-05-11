using System.Diagnostics;
using System.Globalization;

using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;

using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.LoadTest;

/// <summary>
/// Composes the minimum slice of the platform required to drive the
/// REST submit → WAL durable → bot ER receive pipeline end-to-end:
/// dispatcher + WAL (real <see cref="FileEventStore"/>), the books and
/// registries the submit/ER paths mutate, the
/// <see cref="OrderSubmissionService"/>, the
/// <see cref="ExecutionReportProcessor"/>, our synthetic
/// <see cref="LoopbackFillGateway"/>, and the latency-capturing sink.
///
/// <para>
/// We deliberately do <b>not</b> pull in <c>WebApplicationFactory</c>
/// or the full <c>HostBuilder</c> stack — the RFC §7.2 charter is to
/// "isolate platform throughput from Kestrel" and pulling Kestrel into
/// the timing path would mix the measurement with HTTP overhead. A
/// follow-up sub-issue can add an <c>--http</c> mode if we ever need
/// to characterise the API surface specifically.
/// </para>
/// </summary>
public sealed class LoadTestRig : IAsyncDisposable
{
    private readonly LoadTestOptions _opts;
    private readonly string _walDir;
    private readonly bool _walDirOwned;
    private readonly FileEventStore _store;
    private readonly EventDispatcher _dispatcher;
    private readonly OrderSubmissionService _submitter;
    private readonly LoopbackFillGateway _gateway;
    private readonly LatencyCapturingSink _sink;
    private readonly LatencySampleStore _samples;
    private readonly EndClientId _endClient;
    private readonly string _firmId = "LOAD";
    private readonly string _symbol = "PETR4";
    private readonly ulong _securityId = 1;

    private LoadTestRig(
        LoadTestOptions opts,
        string walDir,
        bool walDirOwned,
        FileEventStore store,
        EventDispatcher dispatcher,
        OrderSubmissionService submitter,
        LoopbackFillGateway gateway,
        LatencyCapturingSink sink,
        LatencySampleStore samples,
        EndClientId endClient)
    {
        _opts = opts;
        _walDir = walDir;
        _walDirOwned = walDirOwned;
        _store = store;
        _dispatcher = dispatcher;
        _submitter = submitter;
        _gateway = gateway;
        _sink = sink;
        _samples = samples;
        _endClient = endClient;
    }

    public static Task<LoadTestRig> BootAsync(LoadTestOptions opts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opts);

        // Decide WAL dir: caller-supplied OR per-run temp dir we own.
        // Even when the caller supplies a directory we always create a
        // fresh per-run subdirectory under it so we cannot append to an
        // existing WAL with a fresh ClOrdId counter — that combination
        // would generate ClOrdIds already present in the WAL and
        // violate the ClOrdId monotonicity invariant from RFC §4.4. The
        // supplied path then plays the role of a "WAL root" (e.g.
        // /dev/shm/b3-loadtest) under which each run is isolated.
        string walDir;
        bool owned;
        var runId = "run-" + Guid.NewGuid().ToString("N")[..12];
        if (opts.WalDirectory is { } supplied)
        {
            Directory.CreateDirectory(supplied);
            walDir = Path.Combine(supplied, runId);
            Directory.CreateDirectory(walDir);
            owned = false;
        }
        else
        {
            walDir = Path.Combine(Path.GetTempPath(), "b3-loadtest-" + runId);
            Directory.CreateDirectory(walDir);
            owned = true;
        }

        var persistence = new PersistenceOptions
        {
            Enabled = true,
            DataDirectory = walDir,
            FirmId = "loadtest",
        };
        var store = new FileEventStore(persistence, NullLogger<FileEventStore>.Instance);
        var dispatcher = new EventDispatcher(store);

        var endClients = new EndClientRegistry();
        var endClient = endClients.Register("loadtest");

        var clOrdIds = new ClOrdIdPrefixRegistry();
        // Pre-allocate the prefix so the producer knows the dense
        // counter-only index space in the sample store.
        clOrdIds.AllocatePrefix(endClient);

        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();

        var samples = new LatencySampleStore(ComputeCapacity(opts));
        var sink = new LatencyCapturingSink(samples, opts.Bots);
        var gateway = new LoopbackFillGateway();
        var margin = new NoOpMarginProvider();
        var risk = new RiskPipeline(Array.Empty<IRiskCheck>());
        var accountant = new CompositeRiskAccountant(Array.Empty<IRiskAccountant>());
        IDrainGate drain = new NeverDrainingGate();

        var processor = new ExecutionReportProcessor(
            ownership, book, positions, sink, margin,
            NullLogger<ExecutionReportProcessor>.Instance);
        gateway.Bind(processor, dispatcher);

        var submitter = new OrderSubmissionService(
            clOrdIds, ownership, book, gateway, sink, risk, margin, accountant,
            dispatcher, drain, NullLogger<OrderSubmissionService>.Instance);

        return Task.FromResult(new LoadTestRig(
            opts, walDir, owned, store, dispatcher, submitter, gateway, sink, samples, endClient));
    }

    public async Task<LoadTestReport> RunAsync(CancellationToken ct)
    {
        // Warmup — drives the JIT, fills caches, primes the WAL writer.
        if (_opts.Warmup > TimeSpan.Zero)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[warmup] {_opts.Warmup.TotalSeconds:N1}s @ {_opts.RatePerSecond:N0} msg/s × {_opts.Concurrency}"));
            await DriveAsync(_opts.Warmup, measure: false, ct).ConfigureAwait(false);
            // Reset counters so the steady-state report reflects only
            // measurement-phase activity. Sample slots indexed by ClOrdId
            // counter are NOT reset — the steady-state allocations write
            // into fresh slots past the warmup high-water mark, which the
            // pre-sized capacity in ComputeCapacity already accounts for.
            Interlocked.Exchange(ref _gateway.ErsApplied, 0);
            Interlocked.Exchange(ref _gateway.ErDispatchFailures, 0);
            Interlocked.Exchange(ref _sink.PublishCount, 0);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[steady] {_opts.Duration.TotalSeconds:N1}s @ {_opts.RatePerSecond:N0} msg/s × {_opts.Concurrency}"));
        var (submitted, accepted, rejected, elapsed) =
            await DriveAsync(_opts.Duration, measure: true, ct).ConfigureAwait(false);

        // Quiesce: give the loopback gateway and the WAL writer a moment
        // to flush in-flight ERs. Without this the tail of the latency
        // distribution is empty for samples whose Publish is still
        // queued behind the dispatcher.
        var quiesceDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        var lastReady = _samples.FinalisedCount;
        while (DateTime.UtcNow < quiesceDeadline)
        {
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            var now = _samples.FinalisedCount;
            if (now >= accepted) break;
            if (now == lastReady) break; // no progress for 50ms
            lastReady = now;
        }

        await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        var latencies = _samples.CopyLatencies();
        return new LoadTestReport
        {
            SubmittedCount = submitted,
            AcceptedCount = accepted,
            RejectedCount = rejected,
            ErsApplied = Interlocked.Read(ref _gateway.ErsApplied),
            ErDispatchFailures = Interlocked.Read(ref _gateway.ErDispatchFailures),
            PublishCount = Interlocked.Read(ref _sink.PublishCount),
            ElapsedSeconds = elapsed.TotalSeconds,
            LatencyTicks = latencies,
            TicksPerSecond = Stopwatch.Frequency,
        };
    }

    private async Task<(long Submitted, long Accepted, long Rejected, TimeSpan Elapsed)>
        DriveAsync(TimeSpan duration, bool measure, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        // Per-producer interval is computed in floating point off the
        // global target rate so a target rate that is positive but
        // smaller than --concurrency does not collapse to "unbounded"
        // via integer truncation. perProducerIntervalTicks=0 means
        // "no rate limit" (i.e. --rate 0 / unbounded mode).
        double perProducerIntervalTicks = 0;
        if (_opts.RatePerSecond > 0)
        {
            var globalIntervalTicks = Stopwatch.Frequency / (double)_opts.RatePerSecond;
            perProducerIntervalTicks = globalIntervalTicks * Math.Max(1, _opts.Concurrency);
        }

        var sw = Stopwatch.StartNew();
        var tasks = new Task<(long Submitted, long Accepted, long Rejected)>[_opts.Concurrency];
        for (var p = 0; p < _opts.Concurrency; p++)
        {
            tasks[p] = Task.Run(() => ProducerLoopAsync(
                deadline, perProducerIntervalTicks, measure, ct), ct);
        }
        var perProducer = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        long submitted = 0, accepted = 0, rejected = 0;
        foreach (var t in perProducer)
        {
            submitted += t.Submitted;
            accepted += t.Accepted;
            rejected += t.Rejected;
        }
        return (submitted, accepted, rejected, sw.Elapsed);
    }

    private async Task<(long Submitted, long Accepted, long Rejected)> ProducerLoopAsync(
        long deadlineTick, double perProducerIntervalTicks, bool measure, CancellationToken ct)
    {
        long submitted = 0, accepted = 0, rejected = 0;
        // Track next-dispatch as a double so non-integer intervals
        // (e.g. 1.5 µs at 100k×8) accumulate without quantisation
        // bias. Quantisation to an integer tick happens only when
        // comparing against Stopwatch.GetTimestamp() below.
        double nextDispatchTick = Stopwatch.GetTimestamp();
        var req = new OrderSubmissionRequest(
            Owner: _endClient,
            FirmId: _firmId,
            Symbol: _symbol,
            SecurityId: _securityId,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            Quantity: 100,
            Price: 10m);

        while (!ct.IsCancellationRequested)
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= deadlineTick) break;

            if (perProducerIntervalTicks > 0)
            {
                if (now < nextDispatchTick)
                {
                    var waitTicks = nextDispatchTick - now;
                    var waitMs = waitTicks * 1000.0 / Stopwatch.Frequency;
                    if (waitMs >= 1)
                        await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                    else
                        Thread.SpinWait(50);
                    continue;
                }
                nextDispatchTick += perProducerIntervalTicks;
            }

            var t0 = Stopwatch.GetTimestamp();
            OrderSubmissionResult result;
            try
            {
                result = await _submitter.SubmitAsync(req, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                rejected++;
                continue;
            }

            submitted++;
            if (result.Kind == OrderSubmissionResultKind.Accepted)
            {
                accepted++;
                if (measure)
                    _samples.RecordSubmit(result.ClOrdId, t0);
            }
            else
            {
                rejected++;
            }
        }

        return (submitted, accepted, rejected);
    }

    /// <summary>
    /// Over-provisions sample-store capacity so the dense ClOrdId
    /// counter index never overflows during warmup + steady state at
    /// the configured rate. Cap to a sane upper bound so an unbounded
    /// rate (rate=0) doesn't allocate gigabytes pre-flight.
    /// </summary>
    public static int ComputeCapacity(LoadTestOptions opts)
    {
        var totalSeconds = (opts.Warmup + opts.Duration).TotalSeconds;
        // Allow producers to outpace targetRate by 4× before we run off
        // the end (the loop is best-effort, not strictly capped).
        long projected = opts.RatePerSecond > 0
            ? (long)(opts.RatePerSecond * totalSeconds * 4)
            : 5_000_000;
        projected = Math.Clamp(projected, 100_000, 50_000_000);
        return (int)projected;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort — temp dir cleanup runs regardless
        }

        if (_walDirOwned)
        {
            try { Directory.Delete(_walDir, recursive: true); }
            catch { /* leave it for postmortem */ }
        }
    }

    private sealed class NeverDrainingGate : IDrainGate
    {
        public bool IsDraining => false;
    }
}
