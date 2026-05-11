using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Covers P5/F7's conditional index-fsync change. The contract is:
/// every batch fsyncs the .log; the .idx is fsync'd only when an index
/// record was written since the last <see cref="SegmentWriter.Flush"/>.
/// We assert this via internal counters rather than syscall tracing —
/// the counters are gated to test/InternalsVisibleTo and exist purely
/// to verify the elision actually happens.
/// </summary>
public class SegmentWriterFsyncTests : IDisposable
{
    private readonly string _root;

    public SegmentWriterFsyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-segwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private SegmentWriter NewWriter(int indexEveryNRecords = 64, int indexEveryNBytes = 4096)
    {
        var log = Path.Combine(_root, "000.log");
        var idx = Path.Combine(_root, "000.idx");
        return new SegmentWriter(log, idx, indexEveryNRecords, indexEveryNBytes, fsyncOnFlush: false);
    }

    private static byte[] Payload(int size)
    {
        var p = new byte[size];
        for (var i = 0; i < size; i++) p[i] = (byte)(i & 0xff);
        return p;
    }

    [Fact]
    public async Task Flush_with_no_index_entry_written_does_not_flush_idx()
    {
        // Index cadence high enough that the few records below never trip it.
        await using var w = NewWriter(indexEveryNRecords: 1024, indexEveryNBytes: 1 << 20);

        for (var seq = 1; seq <= 10; seq++) w.Append(seq, Payload(32), 0);
        w.Flush();

        Assert.Equal(1, w.LogFlushCount);
        Assert.Equal(0, w.IndexFlushCount);
    }

    [Fact]
    public async Task Flush_after_index_entry_written_flushes_idx_once()
    {
        await using var w = NewWriter(indexEveryNRecords: 4, indexEveryNBytes: 1 << 20);

        // 4 records → exactly one index entry written by the cadence.
        for (var seq = 1; seq <= 4; seq++) w.Append(seq, Payload(32), 0);
        w.Flush();

        Assert.Equal(1, w.LogFlushCount);
        Assert.Equal(1, w.IndexFlushCount);
    }

    [Fact]
    public async Task Subsequent_flush_after_index_clear_skips_idx_again()
    {
        await using var w = NewWriter(indexEveryNRecords: 4, indexEveryNBytes: 1 << 20);

        // First batch: 4 records → index entry → both flushed.
        for (var seq = 1; seq <= 4; seq++) w.Append(seq, Payload(32), 0);
        w.Flush();

        // Second batch: 3 records (under cadence) → no index entry → idx fsync elided.
        for (var seq = 5; seq <= 7; seq++) w.Append(seq, Payload(32), 0);
        w.Flush();

        Assert.Equal(2, w.LogFlushCount);
        Assert.Equal(1, w.IndexFlushCount);
    }

    [Fact]
    public void GroupCommitMaxRecords_default_is_512()
    {
        // Pinned by P5/F7. Tests in this assembly that override it
        // explicitly continue to do so; this just guards the default.
        Assert.Equal(512, new PersistenceOptions().GroupCommitMaxRecords);
    }
}
