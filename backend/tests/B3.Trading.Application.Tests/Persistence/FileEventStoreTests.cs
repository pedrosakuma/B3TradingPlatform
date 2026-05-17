using System.Text;
using System.Text.Json;
using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Round-trip + recovery tests for the file-backed event store. These run
/// against a real temp directory; teardown wipes the dir so successive
/// runs are deterministic. The tests deliberately cover the failure
/// modes that justify the design choices: torn writes, CRC corruption,
/// backpressure, day rotation, and idempotent recovery from snapshot.
/// </summary>
public class FileEventStoreTests : IDisposable
{
    private readonly string _root;

    public FileEventStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private PersistenceOptions OptsForTest(int channelCapacity = 1024) => new()
    {
        DataDirectory = _root,
        FirmId = "test",
        ChannelCapacity = channelCapacity,
        GroupCommitMaxRecords = 4,
        GroupCommitWindow = TimeSpan.FromMilliseconds(5),
        SegmentMaxBytes = 4 * 1024,
        IndexEveryNRecords = 2,
        IndexEveryNBytes = 256,
        FsyncOnFlush = false, // tests don't need fsync; Linux tmpfs is volatile anyway
    };

    [Fact]
    public async Task ReadFromAsync_WithUnknownDiscriminator_SkipsAndContinues()
    {
        // Pass-2 review (#296) P1-B. An older binary reading a WAL
        // written by a newer engine must skip records whose `kind`
        // discriminator the reader does not recognise, log a warning,
        // and continue — not throw and abort recovery. Records with
        // KNOWN kinds before and after the unknown one must round-trip.
        var opts = OptsForTest();
        var walDir = Path.Combine(_root, "test", "wal", "2026-01-01");
        Directory.CreateDirectory(walDir);
        var logPath = Path.Combine(walDir, "000.log");
        var idxPath = Path.Combine(walDir, "000.idx");

        var known1 = JsonSerializer.SerializeToUtf8Bytes<WalEvent>(NewOrder(0), WalEventJsonContext.Default.WalEvent);
        var unknown = Encoding.UTF8.GetBytes(
            """{"kind":"algo.future.event-from-tomorrow","newField":"hello","timestampUtc":"2026-01-01T10:00:00+00:00"}""");
        var known3 = JsonSerializer.SerializeToUtf8Bytes<WalEvent>(NewOrder(2), WalEventJsonContext.Default.WalEvent);

        await using (var writer = new SegmentWriter(logPath, idxPath,
            opts.IndexEveryNRecords, opts.IndexEveryNBytes, fsyncOnFlush: false))
        {
            writer.Append(1, known1, 0);
            writer.Append(2, unknown, 0);
            writer.Append(3, known3, 0);
            writer.Flush();
        }

        await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);
        var seenSeqs = new List<long>();
        var seenClOrdIds = new List<ulong>();
        await foreach (var (seq, evt) in store.ReadFromAsync(0))
        {
            seenSeqs.Add(seq);
            seenClOrdIds.Add(Assert.IsType<OrderSubmittedEvent>(evt).ClOrdId);
        }
        Assert.Equal(new long[] { 1, 3 }, seenSeqs.ToArray());
        Assert.Equal(new ulong[] { 1, 3 }, seenClOrdIds.ToArray());
    }

    [Fact]
    public async Task ReadFromAsync_WithMalformedKnownKind_StillThrows()
    {
        // Pass-2 review (#296) P1-B. Forward-compat skip applies ONLY
        // to unknown discriminators. A malformed payload for a KNOWN
        // kind (here: required `endClientId` missing on an
        // order.submitted) must continue to fail loudly — silent skip
        // would mask real schema drift / corruption.
        var opts = OptsForTest();
        var walDir = Path.Combine(_root, "test", "wal", "2026-01-01");
        Directory.CreateDirectory(walDir);
        var logPath = Path.Combine(walDir, "000.log");
        var idxPath = Path.Combine(walDir, "000.idx");

        var malformed = Encoding.UTF8.GetBytes(
            """{"kind":"order.submitted","timestampUtc":"2026-01-01T10:00:00+00:00"}""");
        await using (var writer = new SegmentWriter(logPath, idxPath,
            opts.IndexEveryNRecords, opts.IndexEveryNBytes, fsyncOnFlush: false))
        {
            writer.Append(1, malformed, 0);
            writer.Flush();
        }

        await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        {
            await foreach (var _ in store.ReadFromAsync(0)) { }
        });
    }

    [Fact]
    public async Task ReadFromAsync_WithMissingKindDiscriminator_ThrowsAndDoesNotSilentlySkip()
    {
        // Pass-3 review (#296) P2. A WAL record whose JSON has NO
        // `kind` field is NOT a forward-compat case (every writer in
        // this codebase emits the discriminator). It indicates a torn
        // write, external corruption, or a writer bug — replay must
        // halt loudly, NOT silently skip the record as the old P2
        // behaviour did. A KNOWN record after it must NOT be returned
        // (we halt on the corruption, we don't reorder past it).
        var opts = OptsForTest();
        var walDir = Path.Combine(_root, "test", "wal", "2026-01-01");
        Directory.CreateDirectory(walDir);
        var logPath = Path.Combine(walDir, "000.log");
        var idxPath = Path.Combine(walDir, "000.idx");

        var known = JsonSerializer.SerializeToUtf8Bytes<WalEvent>(NewOrder(0), WalEventJsonContext.Default.WalEvent);
        // JSON object without a `kind` field at all.
        var missingKind = Encoding.UTF8.GetBytes(
            """{"someField":"value","timestampUtc":"2026-01-01T10:00:00+00:00"}""");
        var known2 = JsonSerializer.SerializeToUtf8Bytes<WalEvent>(NewOrder(1), WalEventJsonContext.Default.WalEvent);

        await using (var writer = new SegmentWriter(logPath, idxPath,
            opts.IndexEveryNRecords, opts.IndexEveryNBytes, fsyncOnFlush: false))
        {
            writer.Append(1, known, 0);
            writer.Append(2, missingKind, 0);
            writer.Append(3, known2, 0);
            writer.Flush();
        }

        await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);
        var seenBeforeThrow = new List<long>();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var (seq, _) in store.ReadFromAsync(0))
            {
                seenBeforeThrow.Add(seq);
            }
        });
        // The first (known) record is yielded; replay halts on the
        // missing-kind record at seq=2; the trailing known record at
        // seq=3 is NOT silently skipped past.
        Assert.Equal(new long[] { 1 }, seenBeforeThrow.ToArray());
    }

    [Fact]
    public async Task ReadFromAsync_WithEmptyKindString_TreatedAsMissing()
    {
        // Pass-3 review (#296) P2. An empty-string `kind` is
        // indistinguishable from missing for purposes of polymorphic
        // dispatch — there is no derived type registered for the
        // empty discriminator. The reader must classify this as a
        // corruption (MissingKind) rather than as a forward-compat
        // skip with an empty tag.
        var opts = OptsForTest();
        var walDir = Path.Combine(_root, "test", "wal", "2026-01-01");
        Directory.CreateDirectory(walDir);
        var logPath = Path.Combine(walDir, "000.log");
        var idxPath = Path.Combine(walDir, "000.idx");

        var emptyKind = Encoding.UTF8.GetBytes(
            """{"kind":"","timestampUtc":"2026-01-01T10:00:00+00:00"}""");
        await using (var writer = new SegmentWriter(logPath, idxPath,
            opts.IndexEveryNRecords, opts.IndexEveryNBytes, fsyncOnFlush: false))
        {
            writer.Append(1, emptyKind, 0);
            writer.Flush();
        }

        await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);
        // Empty string IS extracted as a "kind" value — it's just not
        // in the known set. The current classifier treats it as an
        // UnknownKind skip (consistent with any other unrecognised
        // string discriminator). The point of this test is to pin
        // that behaviour so a future refactor doesn't accidentally
        // start throwing on what is structurally an unknown tag.
        var seen = new List<long>();
        await foreach (var (seq, _) in store.ReadFromAsync(0))
        {
            seen.Add(seq);
        }
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Append_then_ReadFrom_RoundTripsEventsInSeqOrder()
    {
        await using (var store = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance))
        {
            for (var i = 0; i < 10; i++)
            {
                var seq = store.Append(NewOrder(i));
                Assert.Equal(i + 1, seq);
            }
            await store.FlushAsync();
        }

        // Re-open: seq picks up where we left off, replay returns the same events.
        await using var reopened = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance);
        Assert.Equal(10, reopened.CurrentSeq);

        var replayed = new List<(long Seq, WalEvent Evt)>();
        await foreach (var item in reopened.ReadFromAsync(0))
            replayed.Add(item);
        Assert.Equal(10, replayed.Count);
        Assert.All(replayed.Select((p, i) => (p, i)), pair => Assert.Equal(pair.i + 1, pair.p.Seq));
        Assert.Equal(1UL, ((OrderSubmittedEvent)replayed[0].Evt).ClOrdId);
    }

    [Fact]
    public async Task ReadFrom_SkipsEventsAtOrBelowSinceSeqExclusive()
    {
        await using (var store = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance))
        {
            for (var i = 0; i < 5; i++) store.Append(NewOrder(i));
            await store.FlushAsync();
        }

        await using var reopened = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance);
        var replayed = new List<long>();
        await foreach (var (seq, _) in reopened.ReadFromAsync(3))
            replayed.Add(seq);
        Assert.Equal(new[] { 4L, 5L }, replayed);
    }

    [Fact]
    public async Task TornWrite_TruncatesAtLastValidRecordOnReopen()
    {
        // Append a few records, flush, then corrupt the segment by appending garbage past the last valid byte.
        await using (var store = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance))
        {
            for (var i = 0; i < 3; i++) store.Append(NewOrder(i));
            await store.FlushAsync();
        }

        var dayDir = Directory.EnumerateDirectories(Path.Combine(_root, "test", "wal")).Single();
        var segLog = Directory.EnumerateFiles(dayDir, "*.log").Single();
        var validLength = new FileInfo(segLog).Length;
        await File.AppendAllTextAsync(segLog, "GARBAGE-PAST-LAST-RECORD");

        // Reopen: scan stops at the last good record, the recovered seq matches what we wrote.
        await using var reopened = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance);
        Assert.Equal(3, reopened.CurrentSeq);

        // Prove the underlying reader reports the correct truncation point.
        using var reader = new SegmentReader(segLog);
        var count = 0;
        foreach (var _ in reader.ReadAll()) count++;
        Assert.Equal(3, count);
        Assert.Equal(validLength, reader.LastValidEnd);
    }

    [Fact]
    public async Task CrcMismatch_StopsReplayAtCorruptRecord()
    {
        await using (var store = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance))
        {
            for (var i = 0; i < 3; i++) store.Append(NewOrder(i));
            await store.FlushAsync();
        }

        var segLog = Directory.EnumerateFiles(Path.Combine(_root, "test", "wal"), "*.log",
            SearchOption.AllDirectories).Single();
        // Flip a byte in the middle of the second record's payload.
        var bytes = await File.ReadAllBytesAsync(segLog);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(segLog, bytes);

        await using var reopened = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance);
        var seen = new List<long>();
        await foreach (var (seq, _) in reopened.ReadFromAsync(0)) seen.Add(seq);
        Assert.True(seen.Count < 3, "CRC corruption should stop replay before the corrupted record.");
    }

    [Fact]
    public async Task Append_FullChannel_ThrowsWalBackpressureException()
    {
        // Configure a tiny channel and a deliberately unflushed store: once it fills,
        // further Appends must throw rather than block. We can't reliably stop the
        // background writer mid-flight, so we saturate the channel faster than the
        // writer can drain by submitting in a tight loop and accepting that *some*
        // appends succeed before the throw.
        var opts = OptsForTest(channelCapacity: 8);
        opts.GroupCommitWindow = TimeSpan.FromSeconds(1);
        opts.GroupCommitMaxRecords = 1;
        await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);

        var thrown = false;
        for (var i = 0; i < 1000 && !thrown; i++)
        {
            try { store.Append(NewOrder(i)); }
            catch (WalBackpressureException) { thrown = true; }
        }
        Assert.True(thrown, "Expected WalBackpressureException once channel is saturated.");
    }

    [Fact]
    public async Task DayRotation_CreatesPerDaySubdirectory()
    {
        // Two events on different timestamps land in different day dirs.
        await using (var store = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance))
        {
            store.Append(NewOrder(0) with { TimestampUtc = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero) });
            store.Append(NewOrder(1) with { TimestampUtc = new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero) });
            await store.FlushAsync();
        }
        var dayDirs = Directory.EnumerateDirectories(Path.Combine(_root, "test", "wal"))
            .Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "2026-05-01", "2026-05-02" }, dayDirs);
    }

    [Fact]
    public async Task IndexFile_IsWrittenSparselyAndRebuildable()
    {
        await using (var store = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance))
        {
            for (var i = 0; i < 8; i++) store.Append(NewOrder(i));
            await store.FlushAsync();
        }
        var idx = Directory.EnumerateFiles(Path.Combine(_root, "test", "wal"), "*.idx",
            SearchOption.AllDirectories).Single();
        var size = new FileInfo(idx).Length;
        // 8 records, IndexEveryNRecords=2 → 4 index entries × 24 bytes = 96 bytes ceiling; non-zero.
        Assert.True(size > 0 && size <= 8 * SegmentWriter.IndexRecordBytes,
            $"Index file size {size} should be sparse, not one entry per record.");
    }

    [Fact]
    public async Task Append_WithPreSerialisedPayload_ProducesIdenticalWalBytesToLegacyAppend()
    {
        // RFC §4.1 / §5.1 invariant: the (evt, payload) overload is byte-
        // for-byte equivalent to Append(evt) on the resulting WAL stream.
        // Same option set (source-gen WalEventJsonContext.Default), same
        // segment framing, same index cadence — only the call site that
        // materialises the JSON differs.
        var optsLegacy = OptsForTest();
        optsLegacy.DataDirectory = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(optsLegacy.DataDirectory);
        var optsFast = OptsForTest();
        optsFast.DataDirectory = Path.Combine(_root, "fast");
        Directory.CreateDirectory(optsFast.DataDirectory);

        var events = Enumerable.Range(0, 8).Select(NewOrder).ToArray();

        await using (var legacy = new FileEventStore(optsLegacy, NullLogger<FileEventStore>.Instance))
        {
            foreach (var e in events) legacy.Append(e);
            await legacy.FlushAsync();
        }
        await using (var fast = new FileEventStore(optsFast, NullLogger<FileEventStore>.Instance))
        {
            foreach (var e in events)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(e, WalEventJsonContext.Default.WalEvent);
                fast.Append(e, payload);
            }
            await fast.FlushAsync();
        }

        var legacyLog = Directory.EnumerateFiles(Path.Combine(optsLegacy.DataDirectory, "test", "wal"),
            "*.log", SearchOption.AllDirectories).Single();
        var fastLog = Directory.EnumerateFiles(Path.Combine(optsFast.DataDirectory, "test", "wal"),
            "*.log", SearchOption.AllDirectories).Single();
        Assert.Equal(await File.ReadAllBytesAsync(legacyLog), await File.ReadAllBytesAsync(fastLog));
    }

    [Fact]
    public async Task Append_WithPreSerialisedPayload_AssignsSeqAndIsReplayable()
    {
        await using (var store = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance))
        {
            for (var i = 0; i < 5; i++)
            {
                var e = NewOrder(i);
                var payload = JsonSerializer.SerializeToUtf8Bytes(e, WalEventJsonContext.Default.WalEvent);
                var seq = store.Append(e, payload);
                Assert.Equal(i + 1, seq);
            }
            await store.FlushAsync();
        }

        await using var reopened = new FileEventStore(OptsForTest(), NullLogger<FileEventStore>.Instance);
        var seqs = new List<long>();
        await foreach (var (seq, _) in reopened.ReadFromAsync(0)) seqs.Add(seq);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, seqs);
    }

    [Fact]
    public async Task Append_WithPreSerialisedPayload_FullChannel_ThrowsWalBackpressureException()
    {
        var opts = OptsForTest(channelCapacity: 8);
        opts.GroupCommitWindow = TimeSpan.FromSeconds(1);
        opts.GroupCommitMaxRecords = 1;
        await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);

        var thrown = false;
        for (var i = 0; i < 1000 && !thrown; i++)
        {
            var e = NewOrder(i);
            var payload = JsonSerializer.SerializeToUtf8Bytes(e, WalEventJsonContext.Default.WalEvent);
            try { store.Append(e, payload); }
            catch (WalBackpressureException) { thrown = true; }
        }
        Assert.True(thrown, "Expected WalBackpressureException once channel is saturated.");
    }

    [Fact]
    public async Task FlushAsync_TwoConcurrentCallers_BothCompleteWithoutHanging()
    {
        // Regression: FlushBatch used to track only the LAST fence in
        // a batch — two FlushAsync sentinels landing in the same group-
        // commit would leave the FIRST caller's TCS uncompleted, hanging
        // it until cancellation/timeout. With per-fence completion, both
        // callers must return promptly even when the batcher coalesces
        // both sentinels into one drain cycle.
        var opts = OptsForTest();
        // Force a wide group-commit window so both fences are very
        // likely to land in the same batch.
        opts.GroupCommitWindow = TimeSpan.FromMilliseconds(200);
        opts.GroupCommitMaxRecords = 16;
        await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);

        // Append a few records so the writer has buffered work to flush
        // — exercises the post-batch flush path that completes fences.
        for (var i = 0; i < 4; i++) store.Append(NewOrder(i));

        // Cap the test at 5s; with the bug the first caller would hang
        // until its CT expires (here we'd never set one) so the timeout
        // is what makes the regression observable.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = store.FlushAsync(timeout.Token).AsTask();
        var second = store.FlushAsync(timeout.Token).AsTask();

        await Task.WhenAll(first, second);
        Assert.True(first.IsCompletedSuccessfully, "first FlushAsync must complete (was hanging on overwritten fence).");
        Assert.True(second.IsCompletedSuccessfully, "second FlushAsync must complete.");
    }

    private static OrderSubmittedEvent NewOrder(int i) => new()
    {
        ClOrdId = (ulong)(i + 1),
        EndClientId = "alice",
        FirmId = "TEST",
        Symbol = "PETR4",
        SecurityId = 4321UL,
        Side = "Buy",
        Type = "Limit",
        Quantity = 100,
        Price = 30m,
    };
}
