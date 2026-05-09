using System.Buffers.Binary;

namespace B3.Trading.EntryPointListener.Framing;

/// <summary>
/// Stateful byte accumulator that reassembles SOFH-framed SBE messages from
/// an arbitrary stream of raw bytes (e.g. TCP segments that arrive in
/// fragments or batches).
///
/// <para>Usage contract: call <see cref="Append"/> after every network read,
/// then drain with <see cref="TryReadFrame"/> in a loop.  The
/// <see cref="SofhFrame.Payload"/> span returned by <c>TryReadFrame</c> is
/// valid until the next <c>Append</c> call.</para>
/// </summary>
internal sealed class SofhFrameReader
{
    private const int InitialCapacity = 4096;

    private byte[] _buf = new byte[InitialCapacity];
    private int _head; // index of first unread byte
    private int _tail; // index past the last written byte

    /// <summary>
    /// Set when the reader encounters an unrecoverable framing error
    /// (invalid encoding type, frame too small, frame too large).
    /// The connection should be terminated immediately.
    /// </summary>
    public bool HasProtocolError { get; private set; }

    /// <summary>Human-readable description of the first framing error, when any.</summary>
    public string? ProtocolErrorMessage { get; private set; }

    /// <summary>Copy <paramref name="data"/> into the internal buffer.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        var current = _tail - _head;
        var available = _buf.Length - _tail;

        if (available < data.Length)
        {
            // Try in-place compaction first when buffer is at least half-empty.
            if (_head > 0 && _buf.Length - current >= data.Length)
            {
                _buf.AsSpan(_head, current).CopyTo(_buf);
                _tail = current;
                _head = 0;
            }
            else
            {
                var newSize = Math.Max(_buf.Length * 2, current + data.Length);
                var newBuf = new byte[newSize];
                _buf.AsSpan(_head, current).CopyTo(newBuf);
                _tail = current;
                _head = 0;
                _buf = newBuf;
            }
        }

        data.CopyTo(_buf.AsSpan(_tail));
        _tail += data.Length;
    }

    /// <summary>
    /// Attempts to read the next complete SOFH-framed message.
    /// Returns <c>false</c> when more data is needed or a protocol error
    /// occurred (check <see cref="HasProtocolError"/>).
    /// The returned <see cref="SofhFrame.Payload"/> span is valid until
    /// the next <see cref="Append"/> call.
    /// </summary>
    public bool TryReadFrame(out SofhFrame frame)
    {
        frame = default;
        if (HasProtocolError) return false;

        var dataLen = _tail - _head;
        if (dataLen < SofhFraming.MinFrameSize) return false;

        var span = _buf.AsSpan(_head, dataLen);

        var messageLength = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var encodingType = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);

        if (encodingType != SofhFraming.SbeLittleEndianEncodingType)
        {
            SetError($"Invalid SOFH encodingType 0x{encodingType:X4}; expected 0x{SofhFraming.SbeLittleEndianEncodingType:X4}.");
            return false;
        }

        if (messageLength < SofhFraming.MinFrameSize)
        {
            SetError($"SOFH messageLength {messageLength} is less than minimum frame size {SofhFraming.MinFrameSize}.");
            return false;
        }

        if (messageLength > SofhFraming.MaxFrameSize)
        {
            SetError($"SOFH messageLength {messageLength} exceeds maximum {SofhFraming.MaxFrameSize}.");
            return false;
        }

        if (dataLen < messageLength) return false; // wait for more data

        var blockLength = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var templateId = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        var schemaId = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        var payload = span[SofhFraming.MinFrameSize..messageLength];

        frame = new SofhFrame(blockLength, templateId, schemaId, version, payload);
        _head += messageLength;
        return true;
    }

    private void SetError(string message)
    {
        HasProtocolError = true;
        ProtocolErrorMessage = message;
    }
}
