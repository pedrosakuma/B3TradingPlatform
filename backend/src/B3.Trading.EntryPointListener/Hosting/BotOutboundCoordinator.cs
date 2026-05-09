using System.Collections.Concurrent;
using B3.Trading.Application.UserBots;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Sub-issue #172 (F). Per-credential outbound state coordinator: holds
/// a <see cref="BotOutboundSeqAllocator"/>, the
/// <see cref="BotOutboundBuffer"/>, the count-since-last-checkpoint
/// counter, and the timestamp of the last outbound message. Singleton —
/// all access is keyed by <see cref="Guid"/> credentialId.
///
/// <para>The coordinator does not know about WAL or about connections;
/// the multiplexer composes it with <see cref="IBotSessionConnectionDirectory"/>
/// and <see cref="IUserBotSessionRegistry"/>.</para>
/// </summary>
public sealed class BotOutboundCoordinator
{
    private readonly ConcurrentDictionary<Guid, PerCredential> _byCredentialId = new();
    private readonly Func<Guid, ulong> _seedSeqLookup;
    private readonly int _bufferCap;

    /// <summary>
    /// Wired by the multiplexer at construction so buffer overflow can
    /// trigger the version-bump path. The setter contract is "must be
    /// set before the first <see cref="GetOrCreateBuffer"/> call"; the
    /// multiplexer's constructor is the single writer.
    /// </summary>
    public Action<Guid>? OnBufferOverflow { get; set; }

    public BotOutboundCoordinator(
        IUserBotSessionRegistry sessions,
        Microsoft.Extensions.Options.IOptions<BotErMultiplexerOptions> options)
        : this(sessions, options?.Value ?? throw new ArgumentNullException(nameof(options)))
    { }

    public BotOutboundCoordinator(
        IUserBotSessionRegistry sessions,
        BotErMultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        _bufferCap = options.OutboundBufferMaxMessages;

        // Seed the allocator from the registry's checkpointed watermark.
        // GetOrCreateAsync on the session registry is async because it
        // may dispatch a BotSessionInitializedEvent on first access; by
        // the time the allocator is consulted (post-Establish), the
        // session row exists, and we read it via a synchronous Get.
        _seedSeqLookup = credentialId =>
        {
            // We block briefly here because the registry's get-or-create
            // is the only path; in steady state the row already exists
            // (Establish ran first) so this is a constant-time hit.
            var task = sessions.GetOrCreateAsync(credentialId, CancellationToken.None);
            return task.IsCompletedSuccessfully
                ? task.Result.LastCheckpointedOutboundSeq
                : task.GetAwaiter().GetResult().LastCheckpointedOutboundSeq;
        };
    }

    /// <summary>Allocates the next outbound seq for <paramref name="credentialId"/>.</summary>
    public ulong AllocateNext(Guid credentialId) => Get(credentialId).Allocator.Allocate();

    /// <summary>
    /// Returns the per-credential outbound buffer, creating it on first
    /// access. The created buffer carries the multiplexer's overflow
    /// callback.
    /// </summary>
    public BotOutboundBuffer GetOrCreateBuffer(Guid credentialId) => Get(credentialId).Buffer;

    /// <summary>
    /// Returns the count of outbound messages since the last call to
    /// <see cref="ResetCounter"/>. Read by the checkpointer to decide
    /// whether to dispatch a <c>BotSessionSeqAdvancedEvent</c>.
    /// </summary>
    public long GetCounter(Guid credentialId) => Interlocked.Read(ref Get(credentialId).Counter);

    /// <summary>
    /// Returns the most recently allocated outbound seq for
    /// <paramref name="credentialId"/>. Used by the checkpointer to
    /// stamp the watermark.
    /// </summary>
    public ulong GetCurrentSeq(Guid credentialId) => Get(credentialId).Allocator.Current;

    /// <summary>Bumps the per-credential counter (called from the multiplexer's route loop).</summary>
    public void RecordOutbound(Guid credentialId) => Interlocked.Increment(ref Get(credentialId).Counter);

    /// <summary>Atomically reads the counter and resets it to zero. Returns the prior value.</summary>
    public long ReadAndResetCounter(Guid credentialId) => Interlocked.Exchange(ref Get(credentialId).Counter, 0);

    /// <summary>Enumerates credentialIds with non-zero counters — checkpointer's iteration target.</summary>
    public IReadOnlyList<Guid> ListActiveCredentials()
    {
        var list = new List<Guid>();
        foreach (var (cid, state) in _byCredentialId)
        {
            if (Interlocked.Read(ref state.Counter) > 0) list.Add(cid);
        }
        return list;
    }

    private PerCredential Get(Guid credentialId)
    {
        return _byCredentialId.GetOrAdd(credentialId, cid =>
        {
            var seed = _seedSeqLookup(cid);
            var allocator = new BotOutboundSeqAllocator(seed);
            var buffer = new BotOutboundBuffer(cid, _bufferCap, c => OnBufferOverflow?.Invoke(c));
            return new PerCredential(allocator, buffer);
        });
    }

    private sealed class PerCredential
    {
        public readonly BotOutboundSeqAllocator Allocator;
        public readonly BotOutboundBuffer Buffer;
        public long Counter;
        public PerCredential(BotOutboundSeqAllocator allocator, BotOutboundBuffer buffer)
        {
            Allocator = allocator;
            Buffer = buffer;
        }
    }
}
