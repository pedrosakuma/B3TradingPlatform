using System.Buffers.Binary;
using System.IO.Hashing;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Streams records out of a single <c>.log</c> segment, validating the
/// length prefix and CRC of each record. Stops at the first torn write
/// (length exceeds remaining bytes, or CRC mismatch) and reports the byte
/// offset of the last good record + 1 via <see cref="LastValidEnd"/>, so
/// the caller can truncate the segment after a crash recovery.
///
/// <para>
/// The reader does <b>not</b> consult the <c>.idx</c> file. The index is a
/// performance accelerator (used only by the EOD materialiser and by
/// future seek-based features); replay during startup re-reads the entire
/// active day directory because that is the same I/O cost as a single
/// snapshot read at participant volumes.
/// </para>
/// </summary>
internal sealed class SegmentReader : IDisposable
{
    private readonly FileStream _log;
    private bool _disposed;

    public SegmentReader(string logPath)
    {
        _log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 8192);
    }

    public long LastValidEnd { get; private set; }

    /// <summary>
    /// Yields each record as a freshly-allocated <c>byte[]</c>. The caller
    /// is expected to deserialise it into a typed event before issuing
    /// the next read.
    /// </summary>
    public IEnumerable<byte[]> ReadAll()
    {
        var header = new byte[SegmentWriter.RecordHeaderBytes];
        while (true)
        {
            var pos = _log.Position;
            var read = _log.Read(header, 0, SegmentWriter.RecordHeaderBytes);
            if (read == 0) { LastValidEnd = pos; yield break; }
            if (read < SegmentWriter.RecordHeaderBytes) { LastValidEnd = pos; yield break; }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

            if (length == 0 || pos + SegmentWriter.RecordHeaderBytes + length > _log.Length)
            {
                // Record header was written but the payload was not — torn write.
                LastValidEnd = pos;
                yield break;
            }

            var payload = new byte[length];
            var payloadRead = _log.Read(payload, 0, payload.Length);
            if (payloadRead < payload.Length) { LastValidEnd = pos; yield break; }

            var actualCrc = Crc32.HashToUInt32(payload);
            if (actualCrc != expectedCrc) { LastValidEnd = pos; yield break; }

            LastValidEnd = _log.Position;
            yield return payload;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Dispose();
    }

    /// <summary>
    /// Truncates a <c>.log</c> file to the given byte count. Used after a
    /// recovery scan that detected a torn record at the tail.
    /// </summary>
    public static void TruncateLog(string logPath, long validEnd)
    {
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.SetLength(validEnd);
        fs.Flush(true);
    }
}
