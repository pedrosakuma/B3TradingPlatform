using BenchmarkDotNet.Attributes;

using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;

using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Benchmarks.Benches;

/// <summary>
/// RFC §7.1 — baseline for the WAL append + group-commit path that
/// F1/F7 target. Runs against tmpfs (<c>/dev/shm</c>) when available so
/// disk seek latency does not dominate; falls back to
/// <see cref="Path.GetTempPath"/> on platforms without a RAM-disk
/// (Windows CI). The bench data dir is created per-iteration in
/// <see cref="GlobalSetup"/> and torn down in <see cref="GlobalCleanup"/>.
///
/// <para>Acceptance gate (RFC §7.3 / F7): post-fix saturated Append rate
/// must be ≥4× baseline. P5/P8 PRs record before/after numbers in their
/// bodies referencing this bench by name.</para>
/// </summary>
[MemoryDiagnoser]
public class WAL_Append_Flush_Bench
{
    /// <summary>
    /// Records appended per <see cref="AppendBatchThenFlush"/> invocation.
    /// Two values keep the matrix small while still exercising both the
    /// "below group-commit window" and "saturating the writer" regimes.
    /// </summary>
    [Params(1, 64)]
    public int BatchSize { get; set; }

    private FileEventStore _store = null!;
    private string _dataDir = null!;
    private WalEvent _evt = null!;

    [GlobalSetup]
    public void Setup()
    {
        var root = Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        _dataDir = Path.Combine(root, "b3-bench-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);

        var opts = new PersistenceOptions
        {
            DataDirectory = _dataDir,
            FirmId = "bench",
            ChannelCapacity = 16384,
            GroupCommitMaxRecords = 64,
            GroupCommitWindow = TimeSpan.FromMilliseconds(10),
            FsyncOnFlush = true,
        };
        _store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);
        _evt = new SymbolHaltToggledEvent
        {
            Symbol = "PETR4",
            Halted = true,
            ActorUserId = "bench",
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { _store.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { /* best-effort */ }
        try { Directory.Delete(_dataDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Benchmark]
    public async Task AppendBatchThenFlush()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            _store.Append(_evt);
        }
        await _store.FlushAsync().ConfigureAwait(false);
    }
}
