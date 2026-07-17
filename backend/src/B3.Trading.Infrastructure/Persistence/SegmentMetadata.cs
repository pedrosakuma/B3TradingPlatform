using System.Buffers.Binary;
using System.IO.Hashing;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure.Persistence;

internal readonly record struct SegmentMetadata(Guid Generation, long FirstSeq)
{
    private static ReadOnlySpan<byte> Magic => "B3WALSEQ"u8;
    public const int EncodedLength = 8 + 4 + 16 + 8 + 4;
    private const int FormatVersion = 1;

    public static byte[] Encode(Guid generation, long firstSeq)
    {
        if (generation == Guid.Empty)
            throw new ArgumentException("WAL generation must not be empty.", nameof(generation));
        if (firstSeq <= 0)
            throw new ArgumentOutOfRangeException(nameof(firstSeq));

        var bytes = new byte[EncodedLength];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        generation.TryWriteBytes(bytes.AsSpan(12, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28), firstSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(36), Crc32.HashToUInt32(bytes.AsSpan(0, 36)));
        return bytes;
    }

    public static SegmentMetadata Decode(ReadOnlySpan<byte> bytes, string path)
    {
        if (bytes.Length != EncodedLength
            || !bytes[..8].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != FormatVersion)
        {
            throw new WalRecoveryException(
                $"WAL segment metadata '{path}' has an unsupported format.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]);
        var actual = Crc32.HashToUInt32(bytes[..36]);
        if (expected != actual)
            throw new WalRecoveryException($"WAL segment metadata '{path}' failed checksum validation.");

        var generation = new Guid(bytes.Slice(12, 16));
        var firstSeq = BinaryPrimitives.ReadInt64LittleEndian(bytes[28..]);
        if (generation == Guid.Empty || firstSeq <= 0)
            throw new WalRecoveryException($"WAL segment metadata '{path}' contains invalid values.");
        return new SegmentMetadata(generation, firstSeq);
    }
}
