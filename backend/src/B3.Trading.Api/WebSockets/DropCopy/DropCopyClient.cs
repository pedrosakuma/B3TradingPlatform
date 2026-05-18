using System.Threading.Channels;

namespace B3.Trading.Api.WebSockets.DropCopy;

/// <summary>
/// Q4.6 (#306). One per active drop-copy WebSocket connection. Owns a
/// bounded outbound channel + per-logical-channel monotonic sequence
/// counter. Mirrors the per-user <see cref="SubscribedClient"/> shape so
/// the wire envelope is identical (snapshot at <c>seq=0</c>, deltas
/// from <c>seq=1</c>), but is scoped on <see cref="FirmId"/> rather
/// than an owner identity — a drop-copy session is firm-fanned and
/// not user-keyed.
/// </summary>
public sealed class DropCopyClient
{
    public const int OutboundCapacity = 4096;

    private readonly Channel<OutboundMessage> _outbound;
    private readonly object _channelsSync = new();
    private readonly Dictionary<string, long> _seqByChannel = new(StringComparer.Ordinal);

    public DropCopyClient(string firmId, string username, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        FirmId = firmId;
        Username = username;
        Role = role;
        Id = Guid.NewGuid();
        _outbound = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public Guid Id { get; }

    /// <summary>Firm being observed — sourced from the JWT firm claim (compliance) or the <c>?firmId=</c> override (admin).</summary>
    public string FirmId { get; }

    /// <summary>JWT <c>sub</c> of the principal that opened the session — captured for audit + diagnostics.</summary>
    public string Username { get; }

    /// <summary>Role claim of the principal that opened the session — captured for audit.</summary>
    public string Role { get; }

    public ChannelReader<OutboundMessage> Reader => _outbound.Reader;

    public bool MarkedForDisconnect { get; private set; }
    public string? DisconnectReason { get; private set; }

    private readonly TaskCompletionSource _disconnectRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes the first time <see cref="Enqueue"/> trips the slow-
    /// consumer guard (or any other internal teardown reason). The hub
    /// awaits this alongside the send/receive loops so that a peer
    /// stuck in <c>ws.SendAsync</c> (TCP write buffer full while the
    /// peer never reads) still triggers the cleanup path — completing
    /// the outbound channel is not enough on its own, since SendLoop
    /// is blocked inside <c>SendAsync</c> rather than at the channel
    /// read. Pass-2 review (#323) P1: without this signal,
    /// <c>manager.Remove</c> never runs for the exact slow-consumer
    /// case the feature is meant to handle.
    /// </summary>
    public Task DisconnectRequested => _disconnectRequested.Task;

    /// <summary>
    /// Registers <paramref name="channel"/> for sequence tracking.
    /// Returns false if the channel was already registered (a redundant
    /// subscribe; the snapshot is implicitly already-sent).
    /// </summary>
    public bool Subscribe(string channel)
    {
        lock (_channelsSync)
        {
            if (_seqByChannel.ContainsKey(channel))
                return false;
            _seqByChannel[channel] = 0;
            return true;
        }
    }

    public bool IsSubscribed(string channel)
    {
        lock (_channelsSync)
            return _seqByChannel.ContainsKey(channel);
    }

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

    public void Enqueue(OutboundMessage message)
    {
        if (MarkedForDisconnect)
            return;

        if (!_outbound.Writer.TryWrite(message))
        {
            MarkedForDisconnect = true;
            DisconnectReason = "slow_consumer_resync_required";
            _outbound.Writer.TryComplete();
            _disconnectRequested.TrySetResult();
        }
    }

    public void Complete()
    {
        _outbound.Writer.TryComplete();
        _disconnectRequested.TrySetResult();
    }
}
