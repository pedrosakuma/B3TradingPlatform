using System.Threading.Channels;
using B3.Trading.Domain;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// One per active WebSocket connection. Owns a bounded outbound channel
/// (write attempts that fail mark the client for disconnect) and a per-
/// logical-channel monotonic sequence counter (snapshot is seq=0; deltas
/// start at 1).
/// </summary>
public sealed class SubscribedClient
{
    public const int OutboundCapacity = 1024;

    private readonly Channel<OutboundMessage> _outbound;
    private readonly object _channelsSync = new();
    private readonly Dictionary<string, long> _seqByChannel = new(StringComparer.Ordinal);

    public SubscribedClient(EndClientId owner, string firmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        Owner = owner;
        FirmId = firmId;
        Id = Guid.NewGuid();
        _outbound = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(OutboundCapacity)
        {
            // Wait mode means TryWrite returns false (instead of silently
            // dropping) when full — that's the signal we use to mark the
            // client for disconnect rather than corrupt the stream.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public Guid Id { get; }
    public EndClientId Owner { get; }
    /// <summary>
    /// Firm context derived from the JWT <c>firm</c> claim at connection
    /// time. Required for fan-out of firm-scoped aggregates such as
    /// <see cref="Algo"/> where the same end-client identifier is per-firm.
    /// </summary>
    public string FirmId { get; }
    public ChannelReader<OutboundMessage> Reader => _outbound.Reader;
    public bool MarkedForDisconnect { get; private set; }
    public string? DisconnectReason { get; private set; }

    public bool IsSubscribed(string channel)
    {
        lock (_channelsSync)
            return _seqByChannel.ContainsKey(channel);
    }

    public bool Subscribe(string channel)
    {
        lock (_channelsSync)
        {
            if (_seqByChannel.ContainsKey(channel))
                return false;
            _seqByChannel[channel] = 0; // next delta will be 1
            return true;
        }
    }

    public bool Unsubscribe(string channel)
    {
        lock (_channelsSync)
            return _seqByChannel.Remove(channel);
    }

    /// <summary>
    /// Reserves the next sequence for <paramref name="channel"/> and returns
    /// it. Returns <c>-1</c> if the client is not subscribed.
    /// </summary>
    public long NextSeq(string channel)
    {
        lock (_channelsSync)
        {
            if (!_seqByChannel.TryGetValue(channel, out var current))
                return -1;
            var next = current + 1;
            _seqByChannel[channel] = next;
            return next;
        }
    }

    /// <summary>
    /// Tries to enqueue a frame. On full channel marks the client for
    /// disconnect — the v1 policy is to never silently drop a delta.
    /// </summary>
    public void Enqueue(OutboundMessage message)
    {
        if (MarkedForDisconnect)
            return;

        if (!_outbound.Writer.TryWrite(message))
        {
            MarkedForDisconnect = true;
            DisconnectReason = "slow_consumer_resync_required";
            _outbound.Writer.TryComplete();
        }
    }

    public void MarkForDisconnect(string reason)
    {
        if (MarkedForDisconnect)
            return;
        MarkedForDisconnect = true;
        DisconnectReason = reason;
        _outbound.Writer.TryComplete();
    }

    public void Complete() => _outbound.Writer.TryComplete();
}
