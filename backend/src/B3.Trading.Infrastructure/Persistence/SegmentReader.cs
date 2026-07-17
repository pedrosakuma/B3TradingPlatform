using System.Buffers.Binary;
using System.IO.Hashing;
using B3.Trading.Application.Persistence;

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

    internal readonly record struct SegmentScanResult(
        long RecordCount,
        long LastValidEnd,
        bool IsValid,
        string? Failure);

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

    public SegmentScanResult ScanThrough(long requiredEnd)
    {
        if (requiredEnd < 0 || requiredEnd > _log.Length)
            return new SegmentScanResult(0, 0, false, "required boundary is outside the log");

        _log.Position = 0;
        var count = 0L;
        var header = new byte[SegmentWriter.RecordHeaderBytes];
        while (_log.Position < requiredEnd)
        {
            var pos = _log.Position;
            var read = _log.Read(header, 0, header.Length);
            if (read != header.Length)
                return new SegmentScanResult(count, pos, false, "torn record header");

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var recordEnd = checked(pos + SegmentWriter.RecordHeaderBytes + length);
            if (length == 0 || recordEnd > requiredEnd)
                return new SegmentScanResult(count, pos, false, "record crosses the committed boundary");

            var payload = new byte[length];
            if (_log.Read(payload, 0, payload.Length) != payload.Length)
                return new SegmentScanResult(count, pos, false, "torn record payload");
            if (Crc32.HashToUInt32(payload) != expectedCrc)
                return new SegmentScanResult(count, pos, false, "CRC mismatch");
            count++;
            LastValidEnd = _log.Position;
        }

        return new SegmentScanResult(count, _log.Position, _log.Position == requiredEnd, null);
    }

    public IReadOnlyList<byte[]> ReadAllThrough(long requiredEnd)
    {
        if (requiredEnd < 0 || requiredEnd > _log.Length)
            throw new WalRecoveryException(
                $"Committed WAL boundary {requiredEnd} is outside log length {_log.Length}.");

        _log.Position = 0;
        var records = new List<byte[]>();
        var header = new byte[SegmentWriter.RecordHeaderBytes];
        while (_log.Position < requiredEnd)
        {
            var pos = _log.Position;
            if (_log.Read(header, 0, header.Length) != header.Length)
                throw new WalRecoveryException(
                    $"Committed WAL has a torn record header at offset {pos}.");

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var recordEnd = checked(pos + SegmentWriter.RecordHeaderBytes + length);
            if (length == 0 || recordEnd > requiredEnd)
                throw new WalRecoveryException(
                    $"Committed WAL record at offset {pos} crosses marker boundary {requiredEnd}.");

            var payload = new byte[length];
            if (_log.Read(payload, 0, payload.Length) != payload.Length)
                throw new WalRecoveryException(
                    $"Committed WAL has a torn record payload at offset {pos}.");
            if (Crc32.HashToUInt32(payload) != expectedCrc)
                throw new WalRecoveryException(
                    $"Committed WAL record at offset {pos} failed CRC validation.");
            records.Add(payload);
            LastValidEnd = _log.Position;
        }

        if (_log.Position != requiredEnd)
            throw new WalRecoveryException(
                $"Committed WAL scan ended at {_log.Position}, expected {requiredEnd}.");
        return records;
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
