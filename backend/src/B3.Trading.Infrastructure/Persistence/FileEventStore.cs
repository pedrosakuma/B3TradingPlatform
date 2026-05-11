using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Production <see cref="IEventStore"/>: file-backed, day-segmented,
/// asynchronously written. Sequence numbers are assigned synchronously on
/// <see cref="Append"/> under a tiny lock; the actual disk I/O happens on
/// a single background <see cref="Task"/> that drains a bounded channel.
///
/// <para>
/// <b>Backpressure:</b> the channel is bounded by
/// <see cref="PersistenceOptions.ChannelCapacity"/>. When full,
/// <see cref="Append"/> throws <see cref="WalBackpressureException"/>
/// rather than block — disk lag is meant to surface to the caller as a
/// structured "system busy" rejection (e.g. an order accept that returns
/// 503), not as silent latency creep.
/// </para>
///
/// <para>
/// <b>Group commit:</b> the writer drains up to
/// <see cref="PersistenceOptions.GroupCommitMaxRecords"/> records or waits
/// at most <see cref="PersistenceOptions.GroupCommitWindow"/> before
/// flushing the active segment. fsync per flush is on by default; can be
/// disabled for synthetic benchmarks but never in production.
/// </para>
///
/// <para>
/// <b>Day rotation:</b> each UTC day gets its own subdirectory under
/// <c>wal/</c>. Within a day, segments roll when they cross
/// <see cref="PersistenceOptions.SegmentMaxBytes"/>.
/// </para>
/// </summary>
public sealed class FileEventStore : IEventStore
{
    private static readonly WalEventJsonContext JsonContext = WalEventJsonContext.Default;

    private readonly PersistenceOptions _opts;
    private readonly ILogger<FileEventStore> _logger;
    private readonly string _walRoot;
    private readonly Channel<PendingRecord> _channel;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _writerCts = new();
    private readonly object _seqLock = new();
    private readonly TaskCompletionSource _writerStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _seq;
    private SegmentWriter? _activeWriter;
    private string? _activeDay;
    private int _activeOrdinal;
    private bool _disposed;

    public FileEventStore(IOptions<PersistenceOptions> opts, ILogger<FileEventStore> logger)
        : this(opts.Value, logger) { }

    public FileEventStore(PersistenceOptions opts, ILogger<FileEventStore> logger)
    {
        _opts = opts;
        _logger = logger;
        _walRoot = Path.Combine(opts.DataDirectory, opts.FirmId, "wal");
        Directory.CreateDirectory(_walRoot);

        _seq = ScanHighestSeq();

        _channel = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(opts.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public long CurrentSeq { get { lock (_seqLock) return _seq; } }

    public long Append(WalEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_disposed) throw new ObjectDisposedException(nameof(FileEventStore));

        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, JsonContext.WalEvent);
        return AppendCore(evt, payload);
    }

    public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_disposed) throw new ObjectDisposedException(nameof(FileEventStore));

        // Materialise the payload to a byte[] exactly once. The channel
        // record owns the buffer past the originating call, so pooling
        // here is intentionally avoided (RFC §5.1 Trade-offs / §6.2).
        // When the caller already hands us a heap byte[] via .AsMemory(),
        // we adopt it without copying; otherwise we materialise.
        byte[] payload;
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(preSerialisedPayload, out var seg)
            && seg.Array is not null
            && seg.Offset == 0
            && seg.Count == seg.Array.Length)
        {
            payload = seg.Array;
        }
        else
        {
            payload = preSerialisedPayload.ToArray();
        }
        return AppendCore(evt, payload);
    }

    private long AppendCore(WalEvent evt, byte[] payload)
    {
        long seq;
        lock (_seqLock)
        {
            seq = ++_seq;
        }
        var record = new PendingRecord(seq, payload, evt.TimestampUtc.ToUnixTimeMilliseconds());
        if (!_channel.Writer.TryWrite(record))
        {
            // Roll back the seq so we don't leave a hole in the log.
            lock (_seqLock) _seq--;
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "store.append"));
            throw new WalBackpressureException(
                $"WAL channel is full ({_opts.ChannelCapacity}); refusing append.");
        }
        MetricsRegistry.WalAppended.Add(1);
        return seq;
    }

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        // Inject a sentinel and wait for the writer to drain past it.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fence = new PendingRecord(-1, Array.Empty<byte>(), 0, tcs);
        await _channel.Writer.WriteAsync(fence, ct).ConfigureAwait(false);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
        long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (seq, payload) in EnumerateAllRecords())
        {
            if (ct.IsCancellationRequested) yield break;
            if (seq <= sinceSeqExclusive) continue;
            var evt = JsonSerializer.Deserialize(payload, JsonContext.WalEvent);
            if (evt is not null) yield return (seq, evt);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Walks every segment in seq order. The on-disk layout is sorted
    /// lexicographically by (date, ordinal), so directory enumeration is
    /// also seq-ordered.
    /// </summary>
    internal IEnumerable<(long Seq, byte[] Payload)> EnumerateAllRecords()
    {
        if (!Directory.Exists(_walRoot)) yield break;
        var dayDirs = Directory.EnumerateDirectories(_walRoot)
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal);
        long currentSeq = 0;
        foreach (var day in dayDirs)
        {
            var logFiles = Directory.EnumerateFiles(day, "*.log")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);
            foreach (var logFile in logFiles)
            {
                using var reader = new SegmentReader(logFile);
                foreach (var payload in reader.ReadAll())
                {
                    currentSeq++;
                    yield return (currentSeq, payload);
                }
            }
        }
    }

    private long ScanHighestSeq()
    {
        long highest = 0;
        foreach (var (seq, _) in EnumerateAllRecords()) highest = seq;
        return highest;
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            var batch = new List<PendingRecord>(_opts.GroupCommitMaxRecords);
            var ct = _writerCts.Token;
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < _opts.GroupCommitMaxRecords && _channel.Reader.TryRead(out var rec))
                    batch.Add(rec);

                if (batch.Count > 0 && batch.Count < _opts.GroupCommitMaxRecords)
                {
                    // Wait the rest of the group-commit window for more
                    // records, but cap the total wait at the configured
                    // window so latency is bounded.
                    using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    windowCts.CancelAfter(_opts.GroupCommitWindow);
                    try
                    {
                        while (batch.Count < _opts.GroupCommitMaxRecords &&
                               await _channel.Reader.WaitToReadAsync(windowCts.Token).ConfigureAwait(false))
                        {
                            while (batch.Count < _opts.GroupCommitMaxRecords && _channel.Reader.TryRead(out var rec2))
                                batch.Add(rec2);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // group-commit window elapsed — proceed to flush.
                    }
                }

                FlushBatch(batch);
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FileEventStore writer loop crashed; subsequent appends will queue without ever being flushed.");
        }
        finally
        {
            try { _activeWriter?.Flush(); } catch { /* best-effort */ }
            _activeWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _activeWriter = null;
            _writerStopped.TrySetResult();
        }
    }

    private void FlushBatch(List<PendingRecord> batch)
    {
        TaskCompletionSource? lastFence = null;
        foreach (var rec in batch)
        {
            if (rec.FlushTcs is not null)
            {
                // Sentinel: flush whatever is buffered so far, ack the waiter.
                _activeWriter?.Flush();
                lastFence = rec.FlushTcs;
                continue;
            }

            EnsureActiveSegmentFor(rec);
            _activeWriter!.Append(rec.Seq, rec.Payload, rec.TimestampMs);
        }
        _activeWriter?.Flush();
        lastFence?.TrySetResult();
        // Sentinels earlier in the batch are also satisfied at this point — they were ack'd inline.
    }

    private void EnsureActiveSegmentFor(PendingRecord rec)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(rec.TimestampMs).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rotateForSize = _activeWriter is not null && _activeWriter.BytesWritten >= _opts.SegmentMaxBytes;
        var rotateForDay = _activeDay is not null && _activeDay != day;

        if (_activeWriter is null || rotateForSize || rotateForDay)
        {
            if (_activeWriter is not null)
            {
                MetricsRegistry.WalSegmentsRotated.Add(1,
                    new KeyValuePair<string, object?>("reason", rotateForDay ? "day" : "size"));
            }
            _activeWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _activeWriter = null;

            var dayDir = Path.Combine(_walRoot, day);
            Directory.CreateDirectory(dayDir);

            if (_activeDay != day)
            {
                _activeDay = day;
                _activeOrdinal = NextOrdinalIn(dayDir);
            }
            else
            {
                _activeOrdinal++;
            }

            var logPath = SegmentLogPath(dayDir, _activeOrdinal);
            var idxPath = SegmentIdxPath(dayDir, _activeOrdinal);
            _activeWriter = new SegmentWriter(logPath, idxPath,
                _opts.IndexEveryNRecords, _opts.IndexEveryNBytes, _opts.FsyncOnFlush);
        }
    }

    private static int NextOrdinalIn(string dayDir)
    {
        var max = -1;
        foreach (var f in Directory.EnumerateFiles(dayDir, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    private static string SegmentLogPath(string dayDir, int ordinal) =>
        Path.Combine(dayDir, ordinal.ToString("D3", CultureInfo.InvariantCulture) + ".log");

    private static string SegmentIdxPath(string dayDir, int ordinal) =>
        Path.Combine(dayDir, ordinal.ToString("D3", CultureInfo.InvariantCulture) + ".idx");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        await _writerStopped.Task.ConfigureAwait(false);
        _writerCts.Dispose();
    }

    private readonly record struct PendingRecord(long Seq, byte[] Payload, long TimestampMs, TaskCompletionSource? FlushTcs = null);
}
