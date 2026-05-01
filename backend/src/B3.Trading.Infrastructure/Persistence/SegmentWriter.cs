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

    private readonly FileStream _log;
    private readonly FileStream _idx;
    private readonly int _indexEveryNRecords;
    private readonly int _indexEveryNBytes;
    private readonly bool _fsyncOnFlush;

    private long _bytesAtLastIndex;
    private int _recordsSinceIndex;
    private bool _disposed;

    public SegmentWriter(string logPath, string idxPath, int indexEveryNRecords, int indexEveryNBytes, bool fsyncOnFlush)
    {
        _log = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 8192);
        _log.Seek(0, SeekOrigin.End);
        _idx = new FileStream(idxPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 4096);
        _idx.Seek(0, SeekOrigin.End);
        _indexEveryNRecords = indexEveryNRecords;
        _indexEveryNBytes = indexEveryNBytes;
        _fsyncOnFlush = fsyncOnFlush;
        _bytesAtLastIndex = _log.Length;
    }

    public long BytesWritten => _log.Length;

    public void Append(long seq, ReadOnlySpan<byte> payload, long timestampMs)
    {
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

    private void WriteIndexEntry(long seq, long offset, long timestampMs)
    {
        Span<byte> rec = stackalloc byte[IndexRecordBytes];
        BinaryPrimitives.WriteInt64LittleEndian(rec, seq);
        BinaryPrimitives.WriteInt64LittleEndian(rec[8..], offset);
        BinaryPrimitives.WriteInt64LittleEndian(rec[16..], timestampMs);
        _idx.Write(rec);
    }

    public void Flush()
    {
        _log.Flush(_fsyncOnFlush);
        _idx.Flush(_fsyncOnFlush);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { Flush(); } catch { /* best-effort on dispose */ }
        await _log.DisposeAsync();
        await _idx.DisposeAsync();
    }
}
