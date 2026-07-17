using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Trading.Conformance.Infrastructure;

internal sealed class FixpWireClient : IAsyncDisposable
{
    private const ushort SchemaIdV6 = 1;
    private readonly TcpClient _tcp;
    private readonly Stream _stream;

    private FixpWireClient(TcpClient tcp, Stream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<FixpWireClient> ConnectAsync(CancellationToken ct)
    {
        var endpoint = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpEndpoint)
            ?? throw new InvalidOperationException($"{PlatformEndpoint.EnvFixpEndpoint} is required.");
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out var port))
            throw new InvalidOperationException($"Invalid FIXP endpoint '{endpoint}'.");
        var host = endpoint[..colon].Trim('[', ']');

        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        Stream stream = tcp.GetStream();
        var tls = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpTls);
        if (string.Equals(tls, "true", StringComparison.OrdinalIgnoreCase) || tls == "1")
        {
            var ssl = new SslStream(stream, false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(host);
            stream = ssl;
        }
        return new FixpWireClient(tcp, stream);
    }

    public async Task<FixpFrame> NegotiateAsync(
        uint sessionId,
        ulong sessionVerId,
        string credential,
        CancellationToken ct)
    {
        await _stream.WriteAsync(BuildNegotiate(sessionId, sessionVerId, credential), ct);
        return await ReadFrameAsync(ct);
    }

    public async Task<FixpFrame> EstablishAsync(
        uint sessionId,
        ulong sessionVerId,
        CancellationToken ct)
    {
        await _stream.WriteAsync(BuildEstablish(sessionId, sessionVerId), ct);
        return await ReadFrameAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    private async Task<FixpFrame> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[12];
        await _stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var encoding = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        Assert.Equal((ushort)0xEB50, encoding);
        Assert.True(length >= 12, $"Invalid SOFH frame length {length}.");
        var payload = new byte[length - 12];
        await _stream.ReadExactlyAsync(payload, ct);
        return new FixpFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)),
            payload);
    }

    private static byte[] BuildNegotiate(uint sessionId, ulong sessionVerId, string credential)
    {
        var token = Encoding.UTF8.GetBytes(credential);
        Assert.InRange(token.Length, 1, byte.MaxValue);
        var body = new byte[NegotiateData.BLOCK_LENGTH + 1 + token.Length];
        ref var msg = ref MemoryMarshal.AsRef<NegotiateData>(body.AsSpan(0, NegotiateData.BLOCK_LENGTH));
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        body[NegotiateData.BLOCK_LENGTH] = (byte)token.Length;
        token.CopyTo(body, NegotiateData.BLOCK_LENGTH + 1);
        return Frame((ushort)NegotiateData.BLOCK_LENGTH, (ushort)NegotiateData.MESSAGE_ID, version: 6, body);
    }

    private static byte[] BuildEstablish(uint sessionId, ulong sessionVerId)
    {
        var body = new byte[EstablishData.BLOCK_LENGTH];
        ref var msg = ref MemoryMarshal.AsRef<EstablishData>(body.AsSpan());
        msg.SessionID = (SessionID)sessionId;
        msg.SessionVerID = (SessionVerID)sessionVerId;
        return Frame((ushort)EstablishData.BLOCK_LENGTH, (ushort)EstablishData.MESSAGE_ID, version: 6, body);
    }

    private static byte[] Frame(ushort blockLength, ushort templateId, ushort version, byte[] body)
    {
        var frame = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 0xEB50);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), blockLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), templateId);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), SchemaIdV6);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), version);
        body.CopyTo(frame, 12);
        return frame;
    }
}

internal sealed record FixpFrame(ushort BlockLength, ushort TemplateId, byte[] Payload)
{
    public T Decode<T>() where T : struct => MemoryMarshal.Read<T>(Payload);
}
