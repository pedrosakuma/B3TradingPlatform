using BenchmarkDotNet.Attributes;

using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;

using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Benchmarks.Benches;

/// <summary>
/// RFC §7.1 — baseline for the WAL append + group-commit path that
/// F1/F7 target. Harness v2 (#228) parametrises the WAL data root so
/// the same bench gates both the no-fsync ceiling (tmpfs) and the
/// real-disk fsync path (P5 #199).
///
/// <para><b>DataRoot semantics.</b>
/// <list type="bullet">
///   <item><c>/dev/shm</c> — Linux tmpfs. <c>fsync</c> is effectively a
///     no-op here, so this measures the in-process append + group-commit
///     ceiling, NOT the fsync cost. Numbers from this row are <i>not</i>
///     comparable with disk-backed rows.</item>
///   <item><c>/tmp</c> (or any disk-backed path) — exercises the real
///     <c>fsync</c> path. This is the row P5 (#199) gates against; PR #214
///     could not see a delta because only the tmpfs row was measured.</item>
/// </list>
/// The defaults are <c>/dev/shm</c> + <c>/tmp</c>; CI/operators can override
/// the matrix with <c>B3_BENCH_WAL_PATHS=/dev/shm,/tmp,/var/lib/b3</c>
/// (comma-separated, evaluated at process start by
/// <see cref="DataRootValuesProvider"/>). Paths whose parent directory
/// does not exist are skipped silently to keep the matrix portable.
/// </para>
///
/// <para>Acceptance gate (RFC §7.3 / F7): post-fix saturated Append rate
/// must be ≥4× baseline on the real-disk row. P5/P8 PRs record before/after
/// numbers in their bodies referencing this bench by name.</para>
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

    /// <summary>
    /// WAL data root. See class doc for semantics. Populated by
    /// <see cref="DataRootValuesProvider"/> from
    /// <c>B3_BENCH_WAL_PATHS</c> or, when unset, from
    /// <see cref="DefaultDataRoots"/>.
    /// </summary>
    [ParamsSource(nameof(DataRootValues))]
    public string DataRoot { get; set; } = null!;

    /// <summary>
    /// Defaults when <c>B3_BENCH_WAL_PATHS</c> is not set: tmpfs (no-fsync
    /// ceiling) + a disk-backed path (real fsync). Filtered to existing
    /// roots at enumeration time so Windows CI silently degrades to
    /// <see cref="Path.GetTempPath"/>.
    /// </summary>
    private static readonly string[] DefaultDataRoots =
        new[] { "/dev/shm", "/tmp" };

    /// <summary>
    /// BenchmarkDotNet picks up <c>[ParamsSource]</c> values from a
    /// public property/field; this exposes the resolved list (env-var
    /// overridable) to the runtime.
    /// </summary>
    public static IEnumerable<string> DataRootValues => DataRootValuesProvider();

    private static IEnumerable<string> DataRootValuesProvider()
    {
        var env = Environment.GetEnvironmentVariable("B3_BENCH_WAL_PATHS");
        IEnumerable<string> candidates = !string.IsNullOrWhiteSpace(env)
            ? env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : DefaultDataRoots;

        var any = false;
        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
            {
                any = true;
                yield return c;
            }
        }

        // Last-resort fallback so the bench class is always runnable
        // (e.g. Windows CI where neither /dev/shm nor /tmp exist).
        if (!any)
        {
            yield return Path.GetTempPath();
        }
    }

    private FileEventStore _store = null!;
    private string _dataDir = null!;
    private WalEvent _evt = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dataDir = Path.Combine(DataRoot, "b3-bench-wal-" + Guid.NewGuid().ToString("N"));
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
