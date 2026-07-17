using System.Buffers.Binary;
using System.IO.Hashing;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// One day's worth of event log lives under a single subdirectory; inside
/// it, segments are paired files <c>NNN.log</c> + <c>NNN.idx</c>. The
/// writer always appends to the highest-ordinal segment for the current
/// day; rotation happens when the active <c>.log</c> would cross
/// <see cref="PersistenceOptions.SegmentMaxBytes"/>, or implicitly at the
/// next UTC day boundary (handled by <c>FileEventStore</c>).
///
/// <para>
/// Record framing on <c>.log</c>:
/// <c>[u32 length][u32 crc32][payload bytes]</c> — little-endian, length
/// covers payload only. CRC is computed over the payload, not over the
/// length prefix. This lets the reader detect torn writes deterministically:
/// a length that runs past EOF or a CRC mismatch both signal "the previous
/// flush did not complete; truncate here".
/// </para>
///
/// <para>
/// Index framing on <c>.idx</c>: fixed 24-byte records
/// <c>[u64 seq][u64 offsetInLog][u64 timestampUnixMs]</c>, also little-endian.
/// Sparse: the writer adds an entry once it has either appended
/// <c>IndexEveryNRecords</c> records or written <c>IndexEveryNBytes</c>
/// bytes since the last index entry, whichever happens first. This caps
/// the index at &lt;1% of log size at the configured defaults.
/// </para>
/// </summary>
internal sealed class SegmentWriter : IAsyncDisposable
{
    public const int RecordHeaderBytes = 8; // length(4) + crc32(4)
    public const int IndexRecordBytes = 24; // seq(8) + offset(8) + ts(8)
    public const string FirstSeqSidecarSuffix = ".firstseq";

    private readonly FileStream _log;
    private readonly FileStream _idx;
    private readonly string _logPath;
    private readonly int _indexEveryNRecords;
    private readonly int _indexEveryNBytes;
    private readonly bool _fsyncOnFlush;
    private readonly Guid _generation;

    private long _bytesAtLastIndex;
    private int _recordsSinceIndex;
    private bool _indexDirty;
    private bool _disposed;
    private bool _firstSeqWritten;

    // Test seam: internal counters of how many times we've actually issued
    // a Flush(_fsyncOnFlush) on the underlying log/idx streams. Used by
    // SegmentWriter tests to assert the conditional-index-fsync path
    // (P5/F7) — we must not fsync .idx on batches that wrote no index
    // records. Not exposed publicly; gated by InternalsVisibleTo.
    internal long LogFlushCount;
    internal long IndexFlushCount;

    public SegmentWriter(string logPath, string idxPath, int indexEveryNRecords, int indexEveryNBytes, bool fsyncOnFlush)
        : this(logPath, idxPath, indexEveryNRecords, indexEveryNBytes, fsyncOnFlush, Guid.Empty)
    {
    }

    public SegmentWriter(
        string logPath,
        string idxPath,
        int indexEveryNRecords,
        int indexEveryNBytes,
        bool fsyncOnFlush,
        Guid generation)
    {
        _log = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 8192);
        _log.Seek(0, SeekOrigin.End);
        _idx = new FileStream(idxPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 4096);
        _idx.Seek(0, SeekOrigin.End);
        _logPath = logPath;
        _indexEveryNRecords = indexEveryNRecords;
        _indexEveryNBytes = indexEveryNBytes;
        _fsyncOnFlush = fsyncOnFlush;
        _generation = generation;
        _bytesAtLastIndex = _log.Length;
        // If the segment is being reopened mid-write (recovery / second
        // pass within the same day-dir) and a sidecar already exists,
        // treat it as already-written so we don't overwrite it.
        _firstSeqWritten = File.Exists(logPath + FirstSeqSidecarSuffix) || _log.Length > 0;
    }

    public long BytesWritten => _log.Length;
    public long EndOffset => _log.Position;
    public string LogPath => _logPath;

    public void Append(long seq, ReadOnlySpan<byte> payload, long timestampMs)
    {
        // Pass-1 fix (#328). On the very first append of a fresh
        // segment, persist its starting seq into a sidecar file
        // (<base>.log.firstseq) so the reader can sort segments in
        // strict seq order across day directories. Without this, day
        // directories partitioned by event-timestamp (e.g. late ER
        // arriving with yesterday's timestamp after UTC rollover, or
        // any backdated replay) made EnumerateAllRecords assign
        // synthetic seqs in directory-alphabetical order, diverging
        // from the in-memory _seq — and any reader that capped by
        // CurrentSeq (e.g. StatementEndpoints.BuildAsync past-day
        // path) would slice between an ER and its nested FeeAccrued,
        // producing a torn snapshot.
        if (!_firstSeqWritten)
        {
            WriteFirstSeqSidecar(seq);
            _firstSeqWritten = true;
        }

        Span<byte> header = stackalloc byte[RecordHeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Crc32.HashToUInt32(payload));
        var offset = _log.Position;
        _log.Write(header);
        _log.Write(payload);

        _recordsSinceIndex++;
        var bytesSinceIndex = _log.Position - _bytesAtLastIndex;
        if (_recordsSinceIndex >= _indexEveryNRecords || bytesSinceIndex >= _indexEveryNBytes)
        {
            WriteIndexEntry(seq, offset, timestampMs);
            _recordsSinceIndex = 0;
            _bytesAtLastIndex = _log.Position;
        }
    }

    private void WriteFirstSeqSidecar(long firstSeq)
    {
        // Write to a sibling staging file, fsync it when required, then
        // atomically publish. FileEventStore fsyncs the containing
        // directory before advancing the commit marker.
        var sidecar = _logPath + FirstSeqSidecarSuffix;
        var tmp = sidecar + ".tmp";
        var bytes = _generation == Guid.Empty
            ? LegacyFirstSeq(firstSeq)
            : SegmentMetadata.Encode(_generation, firstSeq);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: bytes.Length))
        {
            fs.Write(bytes);
            if (_fsyncOnFlush) fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, sidecar, overwrite: true);
    }

    private void WriteIndexEntry(long seq, long offset, long timestampMs)
    {
        Span<byte> rec = stackalloc byte[IndexRecordBytes];
        BinaryPrimitives.WriteInt64LittleEndian(rec, seq);
        BinaryPrimitives.WriteInt64LittleEndian(rec[8..], offset);
        BinaryPrimitives.WriteInt64LittleEndian(rec[16..], timestampMs);
        _idx.Write(rec);
        _indexDirty = true;
    }

    public void Flush()
    {
        _log.Flush(_fsyncOnFlush);
        LogFlushCount++;
        // P5/F7: skip the index fsync when no index record has been written
        // since the last flush. The default index cadence is every 64
        // records, so on typical 64-record batches this elides ~63 of every
        // 64 .idx fsyncs. Recovery is unaffected: an unflushed .idx tail
        // just means the next replay rescans a few more .log records to
        // rebuild the in-memory index, which it already does on cold start.
        if (_indexDirty)
        {
            _idx.Flush(_fsyncOnFlush);
            IndexFlushCount++;
            _indexDirty = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Flush();
        }
        finally
        {
            try
            {
                await _log.DisposeAsync();
            }
            finally
            {
                await _idx.DisposeAsync();
            }
        }
    }

    private static byte[] LegacyFirstSeq(long firstSeq)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, firstSeq);
        return bytes;
    }
}
