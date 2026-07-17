using System.Buffers.Binary;
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
/// File-backed segmented WAL with a marker-committed durable prefix. Admission,
/// frame append, log fsync and marker publication are distinct observable
/// boundaries; only the marker prefix is replayable after restart.
/// </summary>
public sealed class FileEventStore : IEventStore, IEventStoreHealth
{
    private const string MarkerFileName = "commit.marker";
    private const string MarkerStagingFileName = "commit.marker.writing";
    private static readonly WalEventJsonContext JsonContext = WalEventJsonContext.Default;

    private readonly PersistenceOptions _opts;
    private readonly ILogger<FileEventStore> _logger;
    private readonly string _walRoot;
    private readonly string _markerPath;
    private readonly string _markerStagingPath;
    private readonly Channel<PendingRecord> _channel;
    private readonly Task _writerTask;
    private readonly object _seqLock = new();
    private readonly object _waiterLock = new();
    private readonly TaskCompletionSource _writerStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SortedDictionary<long, List<TaskCompletionSource>> _commitWaiters = new();
    private readonly HashSet<string> _dirtyDirectories = new(StringComparer.Ordinal);
    private readonly IReconciliationDirectoryDurability _directoryDurability;
    private readonly IWalCommitBoundaryHooks _hooks;

    private readonly Guid _generation;
    private List<WalCommittedSegment> _committedSegments;
    private long _seq;
    private long _lastAppendedSeq;
    private long _lastLogFsyncedSeq;
    private long _lastCommittedSeq;
    private SegmentWriter? _activeWriter;
    private string? _activeDay;
    private int _activeOrdinal;
    private bool _disposed;
    private Exception? _terminalFault;

    public FileEventStore(IOptions<PersistenceOptions> opts, ILogger<FileEventStore> logger)
        : this(opts.Value, logger)
    {
    }

    public FileEventStore(PersistenceOptions opts, ILogger<FileEventStore> logger)
        : this(
            opts,
            logger,
            ReconciliationDirectoryDurability.Instance,
            NoOpWalCommitBoundaryHooks.Instance)
    {
    }

    internal FileEventStore(
        PersistenceOptions opts,
        ILogger<FileEventStore> logger,
        IReconciliationDirectoryDurability directoryDurability,
        IWalCommitBoundaryHooks hooks)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _directoryDurability = directoryDurability
            ?? throw new ArgumentNullException(nameof(directoryDurability));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _walRoot = ResolveWalRoot(opts);
        EnsureDirectoryPath(_walRoot);
        RejectStoragePath(opts.DataDirectory, _walRoot);
        _markerPath = Path.Combine(_walRoot, MarkerFileName);
        _markerStagingPath = Path.Combine(_walRoot, MarkerStagingFileName);

        var recovered = RecoverOrInitialize();
        _generation = recovered.Generation;
        _committedSegments = recovered.Segments.ToList();
        _seq = recovered.LastDurableSeq;
        _lastAppendedSeq = recovered.LastDurableSeq;
        _lastLogFsyncedSeq = recovered.LastDurableSeq;
        _lastCommittedSeq = recovered.LastDurableSeq;

        _channel = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(opts.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public long CurrentSeq => LastAdmittedSeq;
    public Guid WalGeneration => _generation;
    public long LastAdmittedSeq { get { lock (_seqLock) return _seq; } }
    public long LastAppendedSeq => Interlocked.Read(ref _lastAppendedSeq);
    public long LastLogFsyncedSeq => Interlocked.Read(ref _lastLogFsyncedSeq);
    public long LastCommittedSeq => Interlocked.Read(ref _lastCommittedSeq);
    public bool IsHealthy => Volatile.Read(ref _terminalFault) is null;
    public Exception? TerminalFault => Volatile.Read(ref _terminalFault);

    public long Append(WalEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ThrowIfUnavailable();
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, JsonContext.WalEvent);
        return AppendCore(evt, payload);
    }

    public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (preSerialisedPayload.IsEmpty)
            throw new ArgumentException(
                "Pre-serialised WAL payload must not be empty.",
                nameof(preSerialisedPayload));
        ThrowIfUnavailable();
        return AppendCore(evt, preSerialisedPayload.ToArray());
    }

    private long AppendCore(WalEvent evt, byte[] payload)
    {
        lock (_seqLock)
        {
            ThrowIfUnavailable();
            long seq;
            try
            {
                seq = checked(_seq + 1);
            }
            catch (OverflowException ex)
            {
                RecordTerminalFault(ex);
                ThrowIfFaulted();
                throw;
            }

            var record = new PendingRecord(
                seq, payload, evt.TimestampUtc.ToUnixTimeMilliseconds());
            if (!_channel.Writer.TryWrite(record))
            {
                ThrowIfUnavailable();
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "store.append"));
                throw new WalBackpressureException(
                    $"WAL channel is full ({_opts.ChannelCapacity}); refusing append.");
            }
            _seq = seq;
            MetricsRegistry.WalAppended.Add(1);
            return seq;
        }
    }

    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        long target;
        lock (_seqLock)
        {
            ThrowIfUnavailable();
            target = _seq;
        }
        return FlushThroughAsync(target, ct);
    }

    public async ValueTask FlushThroughAsync(long seq, CancellationToken ct = default)
    {
        ThrowIfUnavailable();
        ct.ThrowIfCancellationRequested();
        lock (_seqLock)
        {
            ThrowIfUnavailable();
            if (seq < 0 || seq > _seq)
                throw new ArgumentOutOfRangeException(nameof(seq));
        }
        if (LastCommittedSeq >= seq)
            return;

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterLock)
        {
            ThrowIfFaulted();
            if (LastCommittedSeq >= seq)
                return;
            if (!_commitWaiters.TryGetValue(seq, out var waiters))
            {
                waiters = new List<TaskCompletionSource>();
                _commitWaiters.Add(seq, waiters);
            }
            waiters.Add(waiter);
        }

        // WaitAsync cancels only this await. The shared waiter remains owned by
        // the WAL and is completed/faulted when the prefix actually commits.
        await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
        long sinceSeqExclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (seq, payload) in EnumerateAllRecords())
        {
            if (ct.IsCancellationRequested)
                yield break;
            if (seq <= sinceSeqExclusive)
                continue;
            var outcome = TryDeserialize(payload, out var evt, out var unknownKind);
            if (outcome == DeserializeOutcome.UnknownKind)
            {
                MetricsRegistry.WalUnknownKindSkipped.Add(1,
                    new KeyValuePair<string, object?>("kind", unknownKind ?? "<unknown>"));
                _logger.LogWarning(
                    "FileEventStore: skipping WAL record at seq={Seq} with unknown discriminator kind={Kind}; reader is older than the writer.",
                    seq, unknownKind ?? "<unknown>");
                continue;
            }
            if (outcome == DeserializeOutcome.MissingKind)
            {
                MetricsRegistry.WalMissingKindCorruption.Add(1);
                _logger.LogError(
                    "FileEventStore: WAL record at seq={Seq} has missing or unextractable `kind` discriminator; aborting replay.",
                    seq);
                throw new InvalidDataException(
                    $"FileEventStore: WAL record at seq={seq} has missing or unextractable `kind` discriminator.");
            }
            if (evt is not null)
                yield return (seq, evt);
        }
        await Task.CompletedTask;
    }

    internal enum DeserializeOutcome { Ok, UnknownKind, MissingKind }

    internal static DeserializeOutcome TryDeserialize(
        ReadOnlySpan<byte> payload,
        out WalEvent? evt,
        out string? unknownKind)
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
            if (TryExtractKind(payload, out var kind) && kind is not null)
            {
                if (!KnownDiscriminators.Contains(kind))
                {
                    unknownKind = kind;
                    return DeserializeOutcome.UnknownKind;
                }
                throw;
            }
            return DeserializeOutcome.MissingKind;
        }
    }

    private static bool TryExtractKind(ReadOnlySpan<byte> payload, out string? kind)
    {
        kind = null;
        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return false;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;
                var isKind = reader.ValueTextEquals("kind");
                if (!reader.Read())
                    return false;
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
        }
        return false;
    }

    private static readonly HashSet<string> KnownDiscriminators =
        BuildKnownDiscriminators();

    private static HashSet<string> BuildKnownDiscriminators()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attr in typeof(WalEvent).GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute),
            inherit: false))
        {
            if (attr is System.Text.Json.Serialization.JsonDerivedTypeAttribute jda
                && jda.TypeDiscriminator is string discriminator)
            {
                set.Add(discriminator);
            }
        }
        return set;
    }

    internal IEnumerable<(long Seq, byte[] Payload)> EnumerateAllRecords()
    {
        foreach (var segment in _committedSegments)
        {
            var logPath = SegmentPath(segment.SegmentId);
            using var reader = new SegmentReader(logPath);
            var seq = segment.FirstSeq - 1;
            foreach (var payload in reader.ReadAll())
            {
                seq++;
                if (seq > segment.LastSeq)
                    yield break;
                yield return (seq, payload);
            }
            if (seq != segment.LastSeq)
                throw new WalRecoveryException(
                    $"Committed WAL segment '{segment.SegmentId}' became shorter during replay.");
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            var batch = new List<PendingRecord>(_opts.GroupCommitMaxRecords);
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < _opts.GroupCommitMaxRecords
                       && _channel.Reader.TryRead(out var rec))
                {
                    batch.Add(rec);
                }

                if (batch.Count > 0 && batch.Count < _opts.GroupCommitMaxRecords)
                {
                    using var windowCts = new CancellationTokenSource(_opts.GroupCommitWindow);
                    try
                    {
                        while (batch.Count < _opts.GroupCommitMaxRecords
                               && await _channel.Reader.WaitToReadAsync(windowCts.Token).ConfigureAwait(false))
                        {
                            while (batch.Count < _opts.GroupCommitMaxRecords
                                   && _channel.Reader.TryRead(out var rec))
                            {
                                batch.Add(rec);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
                FlushBatch(batch);
            }

            if (_activeWriter is not null)
            {
                await _activeWriter.DisposeAsync().ConfigureAwait(false);
                _activeWriter = null;
            }
        }
        catch (Exception ex) when (RecordTerminalFault(ex))
        {
            _logger.LogCritical(
                ex,
                "FileEventStore writer loop crashed; the WAL is permanently closed to appends and durability fences.");
        }
        finally
        {
            if (_activeWriter is not null)
            {
                try { await _activeWriter.DisposeAsync().ConfigureAwait(false); } catch { }
                _activeWriter = null;
            }
            _writerStopped.TrySetResult();
        }
    }

    private void FlushBatch(List<PendingRecord> batch)
    {
        if (batch.Count == 0)
            return;

        var nextSegments = _committedSegments.ToList();
        foreach (var rec in batch)
        {
            EnsureActiveSegmentFor(rec);
            _activeWriter!.Append(rec.Seq, rec.Payload, rec.TimestampMs);
            var segmentId = SegmentIdFor(_activeWriter.LogPath);
            ApplySegmentAppend(
                nextSegments, segmentId, rec.Seq, _activeWriter.EndOffset);
            Interlocked.Exchange(ref _lastAppendedSeq, rec.Seq);
            _hooks.OnBoundary(WalCommitBoundary.RecordAppended, rec.Seq);
        }

        var lastSeq = batch[^1].Seq;
        _activeWriter?.Flush();
        FlushDirtyDirectories();
        Interlocked.Exchange(ref _lastLogFsyncedSeq, lastSeq);
        _hooks.OnBoundary(WalCommitBoundary.LogFsynced, lastSeq);

        PublishMarker(
            new WalCommitMarker(_generation, lastSeq, nextSegments),
            invokeHooks: true);
        _committedSegments = nextSegments;
        Interlocked.Exchange(ref _lastCommittedSeq, lastSeq);
        CompleteWaitersThrough(lastSeq);
    }

    private static void ApplySegmentAppend(
        List<WalCommittedSegment> segments,
        string segmentId,
        long seq,
        long endOffset)
    {
        if (segments.Count > 0
            && string.Equals(segments[^1].SegmentId, segmentId, StringComparison.Ordinal))
        {
            var current = segments[^1];
            if (seq != current.LastSeq + 1)
                throw new InvalidDataException("WAL segment append broke sequence contiguity.");
            segments[^1] = current with { LastSeq = seq, EndOffset = endOffset };
            return;
        }

        var expected = segments.Count == 0 ? 1 : checked(segments[^1].LastSeq + 1);
        if (seq != expected)
            throw new InvalidDataException("WAL segment rotation broke sequence contiguity.");
        segments.Add(new WalCommittedSegment(segmentId, seq, seq, endOffset));
    }

    private bool RecordTerminalFault(Exception fault)
    {
        var firstFault = false;
        lock (_seqLock)
        {
            if (Volatile.Read(ref _terminalFault) is null)
            {
                Volatile.Write(ref _terminalFault, fault);
                _channel.Writer.TryComplete(fault);
                firstFault = true;
            }
        }
        if (firstFault)
            FailAllWaiters(fault);
        return true;
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _disposed))
            throw new ObjectDisposedException(nameof(FileEventStore));
        ThrowIfFaulted();
    }

    private void ThrowIfFaulted()
    {
        var fault = Volatile.Read(ref _terminalFault);
        if (fault is not null)
            throw new WalFaultedException(
                "WAL writer is permanently faulted; refusing operation.",
                fault);
    }

    private void CompleteWaitersThrough(long committedSeq)
    {
        List<TaskCompletionSource>? complete = null;
        lock (_waiterLock)
        {
            while (_commitWaiters.Count > 0)
            {
                var first = _commitWaiters.First();
                if (first.Key > committedSeq)
                    break;
                (complete ??= new List<TaskCompletionSource>()).AddRange(first.Value);
                _commitWaiters.Remove(first.Key);
            }
        }
        if (complete is not null)
        {
            foreach (var waiter in complete)
                waiter.TrySetResult();
        }
    }

    private void FailAllWaiters(Exception fault)
    {
        List<TaskCompletionSource>? fail = null;
        lock (_waiterLock)
        {
            foreach (var waiters in _commitWaiters.Values)
                (fail ??= new List<TaskCompletionSource>()).AddRange(waiters);
            _commitWaiters.Clear();
        }
        if (fail is not null)
        {
            foreach (var waiter in fail)
                waiter.TrySetException(new WalFaultedException(
                    "WAL faulted before the requested prefix committed.",
                    fault));
        }
    }

    private void EnsureActiveSegmentFor(PendingRecord rec)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(rec.TimestampMs)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rotateForSize = _activeWriter is not null
            && _activeWriter.BytesWritten >= _opts.SegmentMaxBytes;
        var rotateForDay = _activeDay is not null && _activeDay != day;
        if (_activeWriter is not null && !rotateForSize && !rotateForDay)
            return;

        if (_activeWriter is not null)
        {
            MetricsRegistry.WalSegmentsRotated.Add(1,
                new KeyValuePair<string, object?>(
                    "reason", rotateForDay ? "day" : "size"));
            _activeWriter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _activeWriter = null;
        }

        var dayDir = Path.Combine(_walRoot, day);
        if (!Directory.Exists(dayDir))
        {
            Directory.CreateDirectory(dayDir);
            _dirtyDirectories.Add(_walRoot);
        }
        RejectReparsePoint(dayDir);
        _dirtyDirectories.Add(dayDir);
        if (_activeDay != day)
        {
            _activeDay = day;
            _activeOrdinal = NextOrdinalIn(dayDir);
        }
        else
        {
            _activeOrdinal++;
        }

        _activeWriter = new SegmentWriter(
            SegmentLogPath(dayDir, _activeOrdinal),
            SegmentIdxPath(dayDir, _activeOrdinal),
            _opts.IndexEveryNRecords,
            _opts.IndexEveryNBytes,
            _opts.FsyncOnFlush,
            _generation);
    }

    private void FlushDirtyDirectories()
    {
        if (!_opts.FsyncOnFlush)
        {
            _dirtyDirectories.Clear();
            return;
        }
        foreach (var directory in _dirtyDirectories
                     .OrderByDescending(static p => p.Length))
        {
            _directoryDurability.Flush(directory);
        }
        _dirtyDirectories.Clear();
    }

    private WalCommitMarker RecoverOrInitialize()
    {
        var markerExists = File.Exists(_markerPath);
        var stagingExists = File.Exists(_markerStagingPath);
        var physicalSegments = EnumeratePhysicalSegments();

        if (markerExists)
        {
            var marker = ReadMarker(_markerPath);
            RecoverCommittedPrefix(marker, physicalSegments);
            if (stagingExists)
            {
                File.Delete(_markerStagingPath);
                FlushDirectory(_walRoot);
            }
            return marker;
        }

        if (physicalSegments.Count == 0)
        {
            if (stagingExists)
            {
                File.Delete(_markerStagingPath);
                FlushDirectory(_walRoot);
            }
            var fresh = new WalCommitMarker(
                Guid.NewGuid(), 0, Array.Empty<WalCommittedSegment>());
            PublishMarker(fresh, invokeHooks: false);
            return fresh;
        }

        if (_opts.LegacyWalStartupMode != LegacyWalStartupMode.ControlledCleanShutdown)
        {
            throw new WalLegacyMigrationRequiredException(
                "Non-empty legacy WAL has no commit marker. Refusing to promote CRC-valid survivors after an unknown shutdown; perform the controlled #621 migration or reconciliation.");
        }

        var migrated = MigrateControlledLegacyWal(physicalSegments);
        PublishMarker(migrated, invokeHooks: false);
        if (stagingExists && File.Exists(_markerStagingPath))
        {
            File.Delete(_markerStagingPath);
            FlushDirectory(_walRoot);
        }
        return migrated;
    }

    private WalCommitMarker MigrateControlledLegacyWal(
        IReadOnlyDictionary<string, string> physicalSegments)
    {
        var generation = Guid.NewGuid();
        var legacy = new List<LegacySegment>();
        foreach (var (segmentId, path) in physicalSegments)
        {
            var sidecar = path + SegmentWriter.FirstSeqSidecarSuffix;
            long? hintedFirstSeq = null;
            if (File.Exists(sidecar))
            {
                var bytes = File.ReadAllBytes(sidecar);
                if (bytes.Length != 8)
                    throw new WalRecoveryException(
                        $"Legacy WAL sidecar '{sidecar}' is corrupt or already generation-bound without a marker.");
                hintedFirstSeq = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                if (hintedFirstSeq <= 0)
                    throw new WalRecoveryException($"Legacy WAL sidecar '{sidecar}' has an invalid sequence.");
            }
            legacy.Add(new LegacySegment(segmentId, path, hintedFirstSeq));
        }

        legacy.Sort(LegacySegment.OrderingComparer);
        var manifest = new List<WalCommittedSegment>();
        long nextSeq = 1;
        foreach (var segment in legacy)
        {
            var length = new FileInfo(segment.Path).Length;
            SegmentReader.SegmentScanResult scan;
            using (var reader = new SegmentReader(segment.Path))
                scan = reader.ScanThrough(length);
            if (!scan.IsValid)
                throw new WalRecoveryException(
                    $"Controlled legacy WAL migration found corruption in '{segment.Path}' at offset {scan.LastValidEnd}: {scan.Failure}.");
            if (scan.RecordCount == 0)
            {
                DeleteSegmentArtifacts(segment.Path);
                continue;
            }
            if (segment.HintedFirstSeq is long hinted && hinted != nextSeq)
                throw new WalRecoveryException(
                    $"Legacy WAL sequence sidecar for '{segment.Path}' expected {nextSeq} but contained {hinted}.");

            WriteSegmentMetadata(segment.Path, generation, nextSeq);
            FlushFile(segment.Path);
            var lastSeq = checked(nextSeq + scan.RecordCount - 1);
            manifest.Add(new WalCommittedSegment(
                segment.SegmentId, nextSeq, lastSeq, length));
            nextSeq = checked(lastSeq + 1);
        }
        FlushDirtyDirectories();
        return new WalCommitMarker(generation, nextSeq - 1, manifest);
    }

    private void RecoverCommittedPrefix(
        WalCommitMarker marker,
        IReadOnlyDictionary<string, string> physicalSegments)
    {
        var committedIds = new HashSet<string>(
            marker.Segments.Select(static s => s.SegmentId),
            StringComparer.Ordinal);
        foreach (var segment in marker.Segments)
        {
            ValidateSegmentId(segment.SegmentId);
            if (!physicalSegments.TryGetValue(segment.SegmentId, out var path))
                throw new WalRecoveryException(
                    $"Committed WAL segment '{segment.SegmentId}' is missing.");

            var metadataPath = path + SegmentWriter.FirstSeqSidecarSuffix;
            if (!File.Exists(metadataPath))
                throw new WalRecoveryException(
                    $"Committed WAL segment metadata '{metadataPath}' is missing.");
            var metadata = SegmentMetadata.Decode(
                File.ReadAllBytes(metadataPath), metadataPath);
            if (metadata.Generation != marker.Generation
                || metadata.FirstSeq != segment.FirstSeq)
            {
                throw new WalRecoveryException(
                    $"Committed WAL segment '{segment.SegmentId}' has the wrong generation or first sequence.");
            }

            SegmentReader.SegmentScanResult scan;
            using (var reader = new SegmentReader(path))
                scan = reader.ScanThrough(segment.EndOffset);
            var expectedCount = checked(segment.LastSeq - segment.FirstSeq + 1);
            if (!scan.IsValid || scan.RecordCount != expectedCount)
            {
                throw new WalRecoveryException(
                    $"Committed WAL corruption in '{segment.SegmentId}' at/below marker offset {segment.EndOffset}: {scan.Failure ?? "record count mismatch"}.");
            }

            if (new FileInfo(path).Length > segment.EndOffset)
                TruncateAndFlush(path, segment.EndOffset);
            DeleteDerivedIndex(path);
            DeleteIfExists(metadataPath + ".tmp");
        }

        foreach (var (segmentId, path) in physicalSegments)
        {
            if (!committedIds.Contains(segmentId))
                DeleteSegmentArtifacts(path);
        }
        RemoveEmptyDayDirectories();
        FlushDirtyDirectories();
    }

    private WalCommitMarker ReadMarker(string path)
    {
        try
        {
            return WalCommitMarker.Decode(File.ReadAllBytes(path), path);
        }
        catch (WalRecoveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WalRecoveryException($"Failed to read WAL commit marker '{path}'.", ex);
        }
    }

    private void PublishMarker(WalCommitMarker marker, bool invokeHooks)
    {
        if (invokeHooks)
            _hooks.OnBoundary(WalCommitBoundary.BeforeMarkerStage, marker.LastDurableSeq);
        var payload = marker.Encode();
        using (var stream = new FileStream(
                   _markerStagingPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   _opts.FsyncOnFlush ? FileOptions.WriteThrough : FileOptions.None))
        {
            stream.Write(payload);
            stream.Flush(_opts.FsyncOnFlush);
        }
        FlushDirectory(_walRoot);
        if (invokeHooks)
            _hooks.OnBoundary(
                WalCommitBoundary.MarkerStagedAndFsynced,
                marker.LastDurableSeq);

        File.Move(_markerStagingPath, _markerPath, overwrite: true);
        if (invokeHooks)
            _hooks.OnBoundary(WalCommitBoundary.MarkerPublished, marker.LastDurableSeq);
        FlushDirectory(_walRoot);
        if (invokeHooks)
            _hooks.OnBoundary(
                WalCommitBoundary.MarkerDirectoryFsynced,
                marker.LastDurableSeq);
    }

    private IReadOnlyDictionary<string, string> EnumeratePhysicalSegments()
    {
        var segments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(_walRoot))
        {
            RejectReparsePoint(entry);
            if (File.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (name is MarkerFileName or MarkerStagingFileName)
                    continue;
                throw new WalRecoveryException($"Unexpected artifact in WAL root: '{entry}'.");
            }

            var day = Path.GetFileName(entry);
            if (!DateOnly.TryParseExact(
                    day,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                throw new WalRecoveryException($"Unexpected directory in WAL root: '{entry}'.");
            }

            foreach (var artifact in Directory.EnumerateFileSystemEntries(entry))
            {
                RejectReparsePoint(artifact);
                var name = Path.GetFileName(artifact);
                if (Directory.Exists(artifact))
                    throw new WalRecoveryException($"Unexpected nested WAL directory: '{artifact}'.");
                if (name.EndsWith(".log", StringComparison.Ordinal))
                {
                    var id = day + "/" + name;
                    ValidateSegmentId(id);
                    segments.Add(id, artifact);
                }
                else if (!name.EndsWith(".idx", StringComparison.Ordinal)
                         && !name.EndsWith(".log.firstseq", StringComparison.Ordinal)
                         && !name.EndsWith(".log.firstseq.tmp", StringComparison.Ordinal))
                {
                    throw new WalRecoveryException($"Unexpected WAL segment artifact: '{artifact}'.");
                }
            }
        }
        return segments;
    }

    private void WriteSegmentMetadata(string logPath, Guid generation, long firstSeq)
    {
        var sidecar = logPath + SegmentWriter.FirstSeqSidecarSuffix;
        var staging = sidecar + ".tmp";
        using (var stream = new FileStream(
                   staging,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: SegmentMetadata.EncodedLength,
                   _opts.FsyncOnFlush ? FileOptions.WriteThrough : FileOptions.None))
        {
            stream.Write(SegmentMetadata.Encode(generation, firstSeq));
            stream.Flush(_opts.FsyncOnFlush);
        }
        File.Move(staging, sidecar, overwrite: true);
        _dirtyDirectories.Add(Path.GetDirectoryName(logPath)!);
    }

    private void DeleteSegmentArtifacts(string logPath)
    {
        DeleteIfExists(logPath);
        DeleteIfExists(Path.ChangeExtension(logPath, ".idx"));
        DeleteIfExists(logPath + SegmentWriter.FirstSeqSidecarSuffix);
        DeleteIfExists(logPath + SegmentWriter.FirstSeqSidecarSuffix + ".tmp");
        _dirtyDirectories.Add(Path.GetDirectoryName(logPath)!);
    }

    private void DeleteDerivedIndex(string logPath)
    {
        var idx = Path.ChangeExtension(logPath, ".idx");
        if (File.Exists(idx))
        {
            File.Delete(idx);
            _dirtyDirectories.Add(Path.GetDirectoryName(logPath)!);
        }
    }

    private void TruncateAndFlush(string path, long length)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        stream.Flush(_opts.FsyncOnFlush);
    }

    private void FlushFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(_opts.FsyncOnFlush);
    }

    private void RemoveEmptyDayDirectories()
    {
        foreach (var dayDir in Directory.EnumerateDirectories(_walRoot))
        {
            if (!Directory.EnumerateFileSystemEntries(dayDir).Any())
            {
                Directory.Delete(dayDir);
                _dirtyDirectories.Add(_walRoot);
            }
        }
    }

    private void FlushDirectory(string path)
    {
        if (_opts.FsyncOnFlush)
            _directoryDurability.Flush(path);
    }

    private void EnsureDirectoryPath(string path)
    {
        var missing = new Stack<string>();
        var current = path;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            current = Directory.GetParent(current)?.FullName
                ?? throw new IOException($"Cannot locate existing parent for WAL directory '{path}'.");
        }
        while (missing.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory);
            if (_opts.FsyncOnFlush)
            {
                var parent = Directory.GetParent(directory)?.FullName
                    ?? throw new IOException($"Cannot fsync parent of WAL directory '{directory}'.");
                _directoryDurability.Flush(parent);
            }
        }
    }

    private static string ResolveWalRoot(PersistenceOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.FirmId)
            || Path.IsPathRooted(opts.FirmId)
            || opts.FirmId is "." or ".."
            || opts.FirmId.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("Persistence FirmId must be a relative non-empty path segment.");
        var dataRoot = Path.GetFullPath(opts.DataDirectory);
        var firmRoot = Path.GetFullPath(Path.Combine(dataRoot, opts.FirmId));
        var relative = Path.GetRelativePath(dataRoot, firmRoot);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Persistence FirmId escapes DataDirectory.");
        }
        return Path.Combine(firmRoot, "wal");
    }

    private static void RejectStoragePath(string configuredDataRoot, string walRoot)
    {
        var dataRoot = Path.GetFullPath(configuredDataRoot);
        var current = dataRoot;
        while (true)
        {
            RejectReparsePoint(current);
            if (string.Equals(current, walRoot, StringComparison.Ordinal))
                break;
            var relative = Path.GetRelativePath(current, walRoot);
            var nextComponent = relative.Split(Path.DirectorySeparatorChar)[0];
            current = Path.Combine(current, nextComponent);
        }
    }

    private string SegmentPath(string segmentId)
    {
        ValidateSegmentId(segmentId);
        var parts = segmentId.Split('/');
        var path = Path.GetFullPath(Path.Combine(_walRoot, parts[0], parts[1]));
        var relative = Path.GetRelativePath(_walRoot, path);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new WalRecoveryException($"WAL segment id '{segmentId}' escapes the WAL root.");
        }
        return path;
    }

    private string SegmentIdFor(string logPath)
    {
        var relative = Path.GetRelativePath(_walRoot, Path.GetFullPath(logPath))
            .Replace(Path.DirectorySeparatorChar, '/');
        ValidateSegmentId(relative);
        return relative;
    }

    private static void ValidateSegmentId(string segmentId)
    {
        var parts = segmentId.Split('/');
        if (parts.Length != 2
            || !DateOnly.TryParseExact(
                parts[0],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _)
            || !parts[1].EndsWith(".log", StringComparison.Ordinal)
            || parts[1][..^4].Length == 0
            || !parts[1][..^4].All(char.IsAsciiDigit))
        {
            throw new WalRecoveryException($"Invalid WAL segment id '{segmentId}'.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new WalRecoveryException($"WAL path must not be a symbolic link or reparse point: '{path}'.");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static int NextOrdinalIn(string dayDir)
    {
        var max = -1;
        foreach (var file in Directory.EnumerateFiles(dayDir, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(
                    name,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var ordinal)
                && ordinal > max)
            {
                max = ordinal;
            }
        }
        return checked(max + 1);
    }

    private static string SegmentLogPath(string dayDir, int ordinal) =>
        Path.Combine(
            dayDir,
            ordinal.ToString("D3", CultureInfo.InvariantCulture) + ".log");

    private static string SegmentIdxPath(string dayDir, int ordinal) =>
        Path.Combine(
            dayDir,
            ordinal.ToString("D3", CultureInfo.InvariantCulture) + ".idx");

    public async ValueTask DisposeAsync()
    {
        lock (_seqLock)
        {
            if (!_disposed)
            {
                Volatile.Write(ref _disposed, true);
                _channel.Writer.TryComplete();
            }
        }
        await _writerStopped.Task.ConfigureAwait(false);
    }

    private readonly record struct PendingRecord(
        long Seq,
        byte[] Payload,
        long TimestampMs);

    private readonly record struct LegacySegment(
        string SegmentId,
        string Path,
        long? HintedFirstSeq)
    {
        public static IComparer<LegacySegment> OrderingComparer { get; } =
            Comparer<LegacySegment>.Create(static (a, b) =>
            {
                if (a.HintedFirstSeq.HasValue && b.HintedFirstSeq.HasValue)
                    return a.HintedFirstSeq.Value.CompareTo(b.HintedFirstSeq.Value);
                if (a.HintedFirstSeq.HasValue)
                    return 1;
                if (b.HintedFirstSeq.HasValue)
                    return -1;
                return StringComparer.Ordinal.Compare(a.SegmentId, b.SegmentId);
            });
    }
}
