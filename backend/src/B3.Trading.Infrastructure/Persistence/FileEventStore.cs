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
        if (preSerialisedPayload.IsEmpty)
        {
            // A zero-length record would survive Append (be acknowledged
            // and applied in memory) but recovery treats length==0 as a
            // torn write and stops replay before it — exactly the §4.2
            // "applied in memory but not recoverable" state we forbid.
            throw new ArgumentException(
                "Pre-serialised WAL payload must not be empty.", nameof(preSerialisedPayload));
        }
        if (_disposed) throw new ObjectDisposedException(nameof(FileEventStore));

        // Defensively copy the caller's bytes. The channel record owns
        // the buffer until the writer drains it (well past this call's
        // return), and the public API surface cannot guarantee the
        // caller will not mutate the original array in the meantime.
        // Pooling here is intentionally avoided (RFC §5.1 Trade-offs /
        // §6.2 — pool-leasing across the channel-writer boundary is a
        // known footgun).
        var payload = preSerialisedPayload.ToArray();
        return AppendCore(evt, payload);
    }

    private long AppendCore(WalEvent evt, byte[] payload)
    {
        // Hold _seqLock across both seq assignment and channel enqueue
        // so concurrent direct callers cannot interleave (assign seq A,
        // assign+enqueue seq B, enqueue seq A) and break §4.1's total
        // WAL ordering. TryWrite on a bounded channel is non-blocking,
        // so the critical section stays tiny.
        lock (_seqLock)
        {
            var seq = ++_seq;
            var record = new PendingRecord(seq, payload, evt.TimestampUtc.ToUnixTimeMilliseconds());
            if (!_channel.Writer.TryWrite(record))
            {
                // Roll back the seq so we don't leave a hole in the log.
                _seq--;
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "store.append"));
                throw new WalBackpressureException(
                    $"WAL channel is full ({_opts.ChannelCapacity}); refusing append.");
            }
            MetricsRegistry.WalAppended.Add(1);
            return seq;
        }
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
            var outcome = TryDeserialize(payload, out var evt, out var unknownKind);
            if (outcome == DeserializeOutcome.UnknownKind)
            {
                // Pass-2 review (#296) P1-B. Unknown discriminator
                // (an event-kind added by a newer engine version that
                // hasn't been taught to this binary yet). Skip with a
                // structured warning so an older reader can still
                // traverse a WAL written by a newer engine — the
                // forward-compat contract documented on
                // <see cref="WalEvent"/>. Genuine corruption for a
                // KNOWN kind still surfaces as an exception below.
                MetricsRegistry.WalUnknownKindSkipped.Add(1,
                    new KeyValuePair<string, object?>("kind", unknownKind ?? "<unknown>"));
                _logger.LogWarning(
                    "FileEventStore: skipping WAL record at seq={Seq} with unknown discriminator kind={Kind}; reader is older than the writer.",
                    seq, unknownKind ?? "<unknown>");
                continue;
            }
            if (outcome == DeserializeOutcome.MissingKind)
            {
                // Pass-3 review (#296) P2. Missing-or-unextractable
                // `kind` is NOT a forward-compat case — every WAL
                // writer in this codebase emits the discriminator via
                // the source-gen polymorphic converter, so its
                // absence indicates a torn write, an external
                // corruption, or a writer bug. Surface loudly via a
                // dedicated counter + error log, then throw so replay
                // halts and the operator investigates rather than
                // silently losing records.
                MetricsRegistry.WalMissingKindCorruption.Add(1);
                _logger.LogError(
                    "FileEventStore: WAL record at seq={Seq} has missing or unextractable `kind` discriminator; aborting replay to avoid silent corruption.",
                    seq);
                throw new InvalidDataException(
                    $"FileEventStore: WAL record at seq={seq} has missing or unextractable `kind` discriminator.");
            }
            if (evt is not null) yield return (seq, evt);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Outcome of <see cref="TryDeserialize"/>: distinguishes the
    /// three failure modes the caller must treat differently. Ok
    /// means the payload bound (the out event may still be null if
    /// the converter chose null). UnknownKind means the JSON had a
    /// well-formed string `kind` discriminator that's not in the
    /// reader's known set — a forward-compat skip. MissingKind means
    /// the payload either lacked the `kind` field entirely or could
    /// not be parsed enough to locate it — a corruption signal.
    /// </summary>
    internal enum DeserializeOutcome { Ok, UnknownKind, MissingKind }

    /// <summary>
    /// Pass-2 review (#296) P1-B, refined by Pass-3 P2. Polymorphic
    /// deserialise that distinguishes "unknown discriminator
    /// (forward-compat skip)" from "missing/unextractable
    /// discriminator (corruption — halt replay)" from "JSON malformed
    /// for a known kind (replay-blocking corruption — throw the
    /// original JsonException)". Every other JsonException is
    /// rethrown so torn segments and schema drift remain loud.
    /// </summary>
    internal static DeserializeOutcome TryDeserialize(ReadOnlySpan<byte> payload, out WalEvent? evt, out string? unknownKind)
    {
        evt = null;
        unknownKind = null;
        try
        {
            evt = JsonSerializer.Deserialize(payload, JsonContext.WalEvent);
            return DeserializeOutcome.Ok;
        }
        catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
        {
            // System.Text.Json's polymorphic source-gen surfaces a
            // missing/unknown discriminator as NotSupportedException
            // ("The JSON payload for polymorphic ... type must specify
            // a type discriminator"), while a malformed value for a
            // KNOWN kind surfaces as JsonException. Both funnel here.
            //
            // Inspect the raw JSON for the discriminator.
            //   * present + recognised → re-throw the original
            //     exception (malformed payload for a known kind is
            //     real corruption / schema drift)
            //   * present + unknown    → forward-compat skip
            //   * missing or unextractable → distinct corruption case;
            //     report via MissingKind so the caller throws instead
            //     of silently dropping the record
            if (TryExtractKind(payload, out var kind) && kind is not null)
            {
                if (!KnownDiscriminators.Contains(kind))
                {
                    unknownKind = kind;
                    return DeserializeOutcome.UnknownKind;
                }
                throw;
            }
            unknownKind = null;
            return DeserializeOutcome.MissingKind;
        }
    }

    private static bool TryExtractKind(ReadOnlySpan<byte> payload, out string? kind)
    {
        kind = null;
        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return false;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var isKind = reader.ValueTextEquals("kind");
                if (!reader.Read()) return false;
                if (isKind && reader.TokenType == JsonTokenType.String)
                {
                    kind = reader.GetString();
                    return true;
                }
                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — let the caller's re-throw path handle.
        }
        return false;
    }

    private static readonly HashSet<string> KnownDiscriminators = BuildKnownDiscriminators();

    private static HashSet<string> BuildKnownDiscriminators()
    {
        // Derived from the JsonDerivedType attribute set on WalEvent.
        // Reflection happens exactly once at first access — keeps the
        // discriminator list in lock-step with the type declarations
        // (no second source-of-truth that can drift).
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attr in typeof(WalEvent).GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), inherit: false))
        {
            if (attr is System.Text.Json.Serialization.JsonDerivedTypeAttribute jda
                && jda.TypeDiscriminator is string s)
            {
                set.Add(s);
            }
        }
        return set;
    }

    /// <summary>
    /// Walks every segment in seq order. Segments written by the
    /// current writer carry a <c>&lt;base&gt;.log.firstseq</c> sidecar
    /// (see <see cref="SegmentWriter.FirstSeqSidecarSuffix"/>); when
    /// present, segments are globally sorted by their first seq so the
    /// enumeration order matches the in-memory <c>_seq</c> regardless
    /// of which day directory each segment lives in. This is essential
    /// for readers that cap by <see cref="CurrentSeq"/> (e.g. the
    /// past-day statement projection): the on-disk record order must
    /// agree with the in-memory seq cap or the cap slices the wrong
    /// records. The day-directory layout (partitioned by event
    /// timestamp) intentionally keeps backdated events near their
    /// business day for EOD scoping, so directory-name order alone
    /// cannot reflect append order.
    ///
    /// <para>
    /// Backward compatibility: any segment missing a sidecar (a
    /// legacy WAL from before #328, or a hand-crafted test fixture)
    /// is enumerated with the legacy count-based path — the
    /// per-(dir,name) ordinal sort. Mixing hinted and legacy
    /// segments is supported: legacy segments are processed in their
    /// natural directory-sort order and the running seq counter
    /// carries through.
    /// </para>
    /// </summary>
    internal IEnumerable<(long Seq, byte[] Payload)> EnumerateAllRecords()
    {
        if (!Directory.Exists(_walRoot)) yield break;

        var allSegments = new List<SegmentEntry>();
        foreach (var dayDir in Directory.EnumerateDirectories(_walRoot))
        {
            var dayName = Path.GetFileName(dayDir);
            foreach (var logFile in Directory.EnumerateFiles(dayDir, "*.log"))
            {
                var fileName = Path.GetFileName(logFile);
                long? firstSeq = TryReadFirstSeqSidecar(logFile);
                allSegments.Add(new SegmentEntry(logFile, dayName, fileName, firstSeq));
            }
        }

        // Stable global ordering:
        //   * Segments WITH a sidecar are sorted by their firstSeq.
        //   * Segments WITHOUT a sidecar (legacy or partially-written)
        //     fall back to (dayName, fileName) ordinal sort, and are
        //     interleaved at the position where the running counter
        //     would land — but since we cannot know their firstSeq we
        //     conservatively run them AFTER all hinted segments to
        //     keep the cap-correctness invariant for hinted runs.
        allSegments.Sort(SegmentEntry.OrderingComparer);

        long currentSeq = 0;
        foreach (var seg in allSegments)
        {
            if (seg.FirstSeq is long fs) currentSeq = fs - 1;
            using var reader = new SegmentReader(seg.LogPath);
            foreach (var payload in reader.ReadAll())
            {
                currentSeq++;
                yield return (currentSeq, payload);
            }
        }
    }

    private static long? TryReadFirstSeqSidecar(string logPath)
    {
        var sidecar = logPath + SegmentWriter.FirstSeqSidecarSuffix;
        if (!File.Exists(sidecar)) return null;
        try
        {
            var bytes = File.ReadAllBytes(sidecar);
            if (bytes.Length < 8) return null;
            return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct SegmentEntry(string LogPath, string DayName, string FileName, long? FirstSeq)
    {
        public static readonly IComparer<SegmentEntry> OrderingComparer = Comparer<SegmentEntry>.Create((a, b) =>
        {
            // Unhinted (legacy) segments come FIRST, ordered by the
            // legacy (dayName, fileName) ordinal — their synthetic
            // seqs occupy 1..N. Hinted segments (post-#328) come
            // AFTER, ordered by firstSeq, which by construction is
            // assigned after _seq has been initialised from the
            // legacy tail (see ScanHighestSeq). This preserves
            // upgrade-path correctness when a legacy WAL is opened
            // by the new writer and gains hinted segments on top.
            var ah = a.FirstSeq.HasValue;
            var bh = b.FirstSeq.HasValue;
            if (!ah && !bh)
            {
                var d = StringComparer.Ordinal.Compare(a.DayName, b.DayName);
                if (d != 0) return d;
                return StringComparer.Ordinal.Compare(a.FileName, b.FileName);
            }
            if (!ah) return -1;
            if (!bh) return 1;
            return a.FirstSeq!.Value.CompareTo(b.FirstSeq!.Value);
        });
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
        // Collect every fence's TCS in the batch. A previous version
        // tracked only the LAST fence and dropped earlier ones — two
        // concurrent FlushAsync calls landing in the same batch would
        // hang the first caller until cancellation/timeout because its
        // TCS was overwritten before being completed. Tracking all
        // fences here completes every waiter promptly.
        List<TaskCompletionSource>? fences = null;
        foreach (var rec in batch)
        {
            if (rec.FlushTcs is not null)
            {
                // Sentinel: flush whatever is buffered so far so the
                // ack is honest about what's durable. The TCS itself
                // is completed below, after the post-batch flush, so
                // every fence — first, middle, last — sees the full
                // batch's writes on disk.
                _activeWriter?.Flush();
                (fences ??= new List<TaskCompletionSource>()).Add(rec.FlushTcs);
                continue;
            }

            EnsureActiveSegmentFor(rec);
            _activeWriter!.Append(rec.Seq, rec.Payload, rec.TimestampMs);
        }
        _activeWriter?.Flush();
        if (fences is not null)
        {
            foreach (var f in fences) f.TrySetResult();
        }
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
