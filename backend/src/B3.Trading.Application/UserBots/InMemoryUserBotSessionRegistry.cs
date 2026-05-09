using System.Security.Cryptography;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.UserBots;

/// <summary>
/// In-memory <see cref="IUserBotSessionRegistry"/>. Mutations go through
/// <see cref="EventDispatcher"/> so each (WAL append, in-memory mutation)
/// pair is atomic with respect to snapshot capture. Replay reconstructs
/// state from <see cref="BotSessionInitializedEvent"/> +
/// <see cref="BotSessionVerAdvancedEvent"/> on top of the latest snapshot.
/// Single-active enforcement is in-process; a second host instance would
/// need a distributed lease, out of scope for v0.
/// </summary>
public sealed class InMemoryUserBotSessionRegistry : IUserBotSessionRegistry
{
    private readonly EventDispatcher? _dispatcher;
    private readonly IEventStore? _store;
    private readonly object _gate = new();

    private readonly Dictionary<Guid, BotSessionState> _byCredentialId = new();
    private readonly HashSet<uint> _allocatedSessionIds = new();
    private readonly Dictionary<Guid, string> _activeConnectionByCredentialId = new();

    public InMemoryUserBotSessionRegistry() : this(null, null) { }

    public InMemoryUserBotSessionRegistry(EventDispatcher? dispatcher, IEventStore? store)
    {
        _dispatcher = dispatcher;
        _store = store;
    }

    public Task<BotSessionState> GetOrCreateAsync(Guid credentialId, CancellationToken ct)
    {
        if (credentialId == Guid.Empty)
            throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));

        // Hold _gate across the (check → allocate → dispatch → apply)
        // critical section so concurrent first-access calls for the same
        // credential cannot each allocate a fresh SessionId and emit
        // duplicate BotSessionInitializedEvent records. Monitor is
        // recursive so the apply callback re-entering _gate is safe.
        lock (_gate)
        {
            if (_byCredentialId.TryGetValue(credentialId, out var existing))
                return Task.FromResult(existing);

            var sessionId = AllocateSessionId();
            var created = new BotSessionState(credentialId, sessionId, CurrentVer: 1, LastCheckpointedOutboundSeq: 0);
            var evt = new BotSessionInitializedEvent
            {
                CredentialId = credentialId,
                SessionId = sessionId,
                InitialVer = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            // Reserve the session id eagerly so a future concurrent
            // allocator under the same gate doesn't reuse it before the
            // ApplyInitialized callback runs.
            _allocatedSessionIds.Add(sessionId);

            try
            {
                if (_dispatcher is not null)
                    _dispatcher.Dispatch(evt, () => ApplyInitialized(created));
                else
                    ApplyInitialized(created);
            }
            catch
            {
                // Dispatch failed (WAL backpressure) — release the speculative
                // SessionId reservation so a retry doesn't leak the id.
                if (!_byCredentialId.ContainsKey(credentialId))
                    _allocatedSessionIds.Remove(sessionId);
                throw;
            }

            return Task.FromResult(created);
        }
    }

    public Task<bool> TryClaimActiveAsync(
        Guid credentialId, ulong attemptedVer, string connectionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        lock (_gate)
        {
            if (!_byCredentialId.TryGetValue(credentialId, out var state))
                return Task.FromResult(false);
            if (attemptedVer != state.CurrentVer)
                return Task.FromResult(false);
            if (_activeConnectionByCredentialId.TryGetValue(credentialId, out var existing)
                && !string.Equals(existing, connectionId, StringComparison.Ordinal))
                return Task.FromResult(false);

            _activeConnectionByCredentialId[credentialId] = connectionId;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(Guid credentialId, string connectionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        lock (_gate)
        {
            if (_activeConnectionByCredentialId.TryGetValue(credentialId, out var existing)
                && string.Equals(existing, connectionId, StringComparison.Ordinal))
            {
                _activeConnectionByCredentialId.Remove(credentialId);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<ulong> BumpVersionAsync(Guid credentialId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // Hold _gate across the read + dispatch so concurrent bumps cannot
        // both observe the same oldVer and emit duplicate (oldVer, oldVer+1)
        // events. The dispatcher's lock is acquired *inside* this gate (and
        // its apply callback re-enters _gate, which is safe — Monitor is
        // recursive), giving a strict (read → append → apply) critical
        // section per credential. FlushAsync is awaited *outside* the gate
        // because it is an async I/O fence, not state mutation.
        ulong oldVer;
        ulong newVer;
        BotSessionVerAdvancedEvent evt;

        lock (_gate)
        {
            if (!_byCredentialId.TryGetValue(credentialId, out var state))
                throw new InvalidOperationException(
                    $"Cannot bump version for unknown credential {credentialId}.");

            oldVer = state.CurrentVer;
            newVer = checked(oldVer + 1);
            evt = new BotSessionVerAdvancedEvent
            {
                CredentialId = credentialId,
                OldVer = oldVer,
                NewVer = newVer,
                Reason = reason,
            };

            if (_dispatcher is not null)
                _dispatcher.Dispatch(evt, () => ApplyVerAdvanced(credentialId, newVer));
            else
                ApplyVerAdvanced(credentialId, newVer);
        }

        // RFC §4.8 mandatory durability fence: the bot must not observe
        // newVer (e.g. via EstablishReject) before the WAL has guaranteed
        // it. A crash in this window would otherwise resurrect oldVer on
        // recovery and the bot's reconnect logic would loop indefinitely.
        if (_store is not null)
            await _store.FlushAsync(ct).ConfigureAwait(false);

        return newVer;
    }

    /// <summary>Snapshot capture (called under <c>EventDispatcher.WithSnapshotLock</c>).</summary>
    public IReadOnlyList<BotSessionStateSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _byCredentialId.Values
                .OrderBy(s => s.CredentialId)
                .Select(s => new BotSessionStateSnapshot(
                    s.CredentialId, s.SessionId, s.CurrentVer, s.LastCheckpointedOutboundSeq))
                .ToList();
        }
    }

    /// <summary>Snapshot restore hook — single-threaded at startup.</summary>
    public void Restore(IEnumerable<BotSessionStateSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        lock (_gate)
        {
            _byCredentialId.Clear();
            _allocatedSessionIds.Clear();
            _activeConnectionByCredentialId.Clear();
            foreach (var s in snapshots)
            {
                var state = new BotSessionState(
                    s.CredentialId, s.SessionId, s.CurrentVer, s.LastCheckpointedOutboundSeq);
                _byCredentialId[s.CredentialId] = state;
                _allocatedSessionIds.Add(s.SessionId);
            }
        }
    }

    /// <summary>Replay hook for <see cref="BotSessionInitializedEvent"/>.</summary>
    internal void ApplyInitialized(BotSessionState state)
    {
        lock (_gate)
        {
            _byCredentialId[state.CredentialId] = state;
            _allocatedSessionIds.Add(state.SessionId);
        }
    }

    /// <summary>Replay hook for <see cref="BotSessionVerAdvancedEvent"/>.</summary>
    internal void ApplyVerAdvanced(Guid credentialId, ulong newVer)
    {
        lock (_gate)
        {
            if (_byCredentialId.TryGetValue(credentialId, out var existing))
            {
                _byCredentialId[credentialId] = existing with { CurrentVer = newVer };
            }
            // A bump for an unknown credential during replay would mean a
            // missing initialised event upstream; tolerate silently rather
            // than crash recovery — the next live bump will re-emit if the
            // credential is still in use.
        }
    }

    /// <summary>
    /// Replay hook for <see cref="BotSessionSeqAdvancedEvent"/>. Idempotent
    /// + reordering-safe: only advances the watermark, never regresses it.
    /// </summary>
    internal void ApplyCheckpointedSeq(Guid credentialId, ulong checkpointedSeq)
    {
        lock (_gate)
        {
            if (_byCredentialId.TryGetValue(credentialId, out var existing)
                && checkpointedSeq > existing.LastCheckpointedOutboundSeq)
            {
                _byCredentialId[credentialId] = existing with
                {
                    LastCheckpointedOutboundSeq = checkpointedSeq,
                };
            }
        }
    }

    public void UpdateCheckpointedOutboundSeq(Guid credentialId, ulong checkpointedSeq)
    {
        if (credentialId == Guid.Empty) return;

        BotSessionSeqAdvancedEvent? evt = null;
        lock (_gate)
        {
            if (!_byCredentialId.TryGetValue(credentialId, out var existing))
                return;
            if (checkpointedSeq <= existing.LastCheckpointedOutboundSeq)
                return;

            evt = new BotSessionSeqAdvancedEvent
            {
                CredentialId = credentialId,
                CheckpointedOutboundSeq = checkpointedSeq,
                At = DateTimeOffset.UtcNow,
            };

            // Dispatch under the gate so the apply callback's mutation
            // and the WAL append form a single atomic step w.r.t.
            // snapshot capture (mirrors BumpVersionAsync). No FlushAsync —
            // RFC §4.8 says this is a best-effort watermark.
            if (_dispatcher is not null)
                _dispatcher.Dispatch(evt, () => ApplyCheckpointedSeq(credentialId, checkpointedSeq));
            else
                ApplyCheckpointedSeq(credentialId, checkpointedSeq);
        }
    }

    // RFC §4.5: SessionId is non-zero uint32. Birthday-collisions on
    // 2^32 minus-one are negligible at participant counts; a defensive
    // collision check still avoids re-issuing an id while the lock is held.
    private uint AllocateSessionId()
    {
        Span<byte> buf = stackalloc byte[4];
        for (var attempt = 0; attempt < 64; attempt++)
        {
            RandomNumberGenerator.Fill(buf);
            var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf);
            if (id == 0) continue;
            if (_allocatedSessionIds.Contains(id)) continue;
            return id;
        }
        throw new InvalidOperationException(
            "Exhausted SessionId allocation attempts — the credential pool is implausibly large.");
    }
}
