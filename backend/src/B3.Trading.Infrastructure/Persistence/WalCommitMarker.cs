using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure.Persistence;

internal readonly record struct WalCommittedSegment(
    string SegmentId,
    long FirstSeq,
    long LastSeq,
    long EndOffset);

internal readonly record struct WalCommitMarker(
    Guid Generation,
    long LastDurableSeq,
    IReadOnlyList<WalCommittedSegment> Segments)
{
    private static ReadOnlySpan<byte> Magic => "B3WALCMT"u8;
    private const int FormatVersion = 1;
    private const int HeaderBytes = 8 + 4 + 16 + 8 + 4;
    private const int EntryFixedBytes = 4 + 8 + 8 + 8;
    private const int ChecksumBytes = 4;
    private const int MaxSegmentIdBytes = 512;
    private const int MaxSegments = 100_000;

    public byte[] Encode()
    {
        Validate();
        var encodedIds = new byte[Segments.Count][];
        var length = checked(HeaderBytes + ChecksumBytes);
        for (var i = 0; i < Segments.Count; i++)
        {
            encodedIds[i] = Encoding.UTF8.GetBytes(Segments[i].SegmentId);
            if (encodedIds[i].Length is 0 or > MaxSegmentIdBytes)
                throw new InvalidDataException("WAL marker segment id has an invalid length.");
            length = checked(length + EntryFixedBytes + encodedIds[i].Length);
        }

        var bytes = new byte[length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        Generation.TryWriteBytes(bytes.AsSpan(12, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28), LastDurableSeq);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), Segments.Count);
        var offset = HeaderBytes;
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            var id = encodedIds[i];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), id.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 4), segment.FirstSeq);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 12), segment.LastSeq);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 20), segment.EndOffset);
            id.CopyTo(bytes.AsSpan(offset + EntryFixedBytes));
            offset += EntryFixedBytes + id.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(offset), Crc32.HashToUInt32(bytes.AsSpan(0, offset)));
        return bytes;
    }

    public static WalCommitMarker Decode(ReadOnlySpan<byte> bytes, string path)
    {
        if (bytes.Length < HeaderBytes + ChecksumBytes
            || !bytes[..8].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != FormatVersion)
        {
            throw new WalRecoveryException($"WAL commit marker '{path}' has an unsupported format.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^ChecksumBytes..]);
        var actual = Crc32.HashToUInt32(bytes[..^ChecksumBytes]);
        if (expected != actual)
            throw new WalRecoveryException($"WAL commit marker '{path}' failed checksum validation.");

        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]);
        if (count is < 0 or > MaxSegments)
            throw new WalRecoveryException($"WAL commit marker '{path}' has an invalid segment count.");

        var segments = new List<WalCommittedSegment>(count);
        var offset = HeaderBytes;
        for (var i = 0; i < count; i++)
        {
            if (offset > bytes.Length - ChecksumBytes - EntryFixedBytes)
                throw new WalRecoveryException($"WAL commit marker '{path}' has a truncated segment entry.");
            var idLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            if (idLength is <= 0 or > MaxSegmentIdBytes
                || offset > bytes.Length - ChecksumBytes - EntryFixedBytes - idLength)
            {
                throw new WalRecoveryException($"WAL commit marker '{path}' has an invalid segment id length.");
            }
            segments.Add(new WalCommittedSegment(
                Encoding.UTF8.GetString(bytes.Slice(offset + EntryFixedBytes, idLength)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[(offset + 4)..]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[(offset + 12)..]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[(offset + 20)..])));
            offset += EntryFixedBytes + idLength;
        }
        if (offset != bytes.Length - ChecksumBytes)
            throw new WalRecoveryException($"WAL commit marker '{path}' has trailing bytes.");

        var marker = new WalCommitMarker(
            new Guid(bytes.Slice(12, 16)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[28..]),
            segments);
        try
        {
            marker.Validate();
        }
        catch (InvalidDataException ex)
        {
            throw new WalRecoveryException($"WAL commit marker '{path}' contains invalid values.", ex);
        }
        return marker;
    }

    private void Validate()
    {
        if (Generation == Guid.Empty || LastDurableSeq < 0)
            throw new InvalidDataException("WAL marker contains an invalid generation or sequence.");
        if (LastDurableSeq == 0)
        {
            if (Segments.Count != 0)
                throw new InvalidDataException("An empty WAL marker must not contain segments.");
            return;
        }
        if (Segments.Count == 0 || Segments.Count > MaxSegments)
            throw new InvalidDataException("A non-empty WAL marker must contain a segment manifest.");

        long expected = 1;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in Segments)
        {
            if (!ids.Add(segment.SegmentId)
                || segment.FirstSeq != expected
                || segment.LastSeq < segment.FirstSeq
                || segment.EndOffset < SegmentWriter.RecordHeaderBytes)
            {
                throw new InvalidDataException("WAL marker segment manifest is not a contiguous valid prefix.");
            }
            if (segment.LastSeq == long.MaxValue)
                throw new InvalidDataException("WAL marker sequence space is exhausted.");
            expected = segment.LastSeq + 1;
        }
        if (expected - 1 != LastDurableSeq)
            throw new InvalidDataException("WAL marker sequence does not match its segment manifest.");
    }
}

internal interface IWalCommitBoundaryHooks
{
    void OnBoundary(WalCommitBoundary boundary, long seq);
}

internal enum WalCommitBoundary
{
    RecordAppended,
    LogFsynced,
    BeforeMarkerStage,
    MarkerStagedAndFsynced,
    MarkerPublished,
    MarkerDirectoryFsynced,
}

internal sealed class NoOpWalCommitBoundaryHooks : IWalCommitBoundaryHooks
{
    public static NoOpWalCommitBoundaryHooks Instance { get; } = new();
    public void OnBoundary(WalCommitBoundary boundary, long seq) { }
}
