namespace B3.Trading.Application.UserBots;

/// <summary>
/// Synthetic principal attached to a FIXP connection after a successful
/// <c>Negotiate</c>. Mirrors the user-claim shape produced by JWT auth
/// elsewhere in the platform — sub-issue D wires it onto the listener
/// connection scope so subsequent code (sub-issue E onward) can reuse the
/// same per-user isolation primitives REST and WS already use.
/// </summary>
/// <remarks>
/// <see cref="UserId"/> is the JWT <c>sub</c> string of the credential's
/// owner (see <see cref="UserBotCredential"/>). Logging a principal is
/// safe — none of these fields are secret.
/// </remarks>
public sealed record BotSessionPrincipal(
    string UserId,
    Guid CredentialId,
    string CredShortId,
    string Label,
    string FirmId = "default");

/// <summary>
/// Persistent per-credential state managed by
/// <see cref="IUserBotSessionRegistry"/>: the platform-allocated FIXP
/// <see cref="SessionId"/> (uint32, stable for the credential lifetime),
/// the monotonically advancing <see cref="CurrentVer"/> (uint64,
/// RFC §4.5), and the most recently checkpointed outbound seq watermark
/// used by sub-issues E/G for replay bound checks.
/// </summary>
public sealed record BotSessionState(
    Guid CredentialId,
    uint SessionId,
    ulong CurrentVer,
    ulong LastCheckpointedOutboundSeq);

public sealed record BotSessionVersionAdvance(
    ulong NewVersion,
    string? DisplacedConnectionId);

/// <summary>
/// Per-credential session bookkeeping for the FIXP listener
/// (RFC user-bot-fixp-listener-v0 §4.5 + §4.8). Implementations enforce
/// single-active-session-per-credential, allocate a stable
/// <see cref="BotSessionState.SessionId"/> on first access, and expose a
/// version-bump path that durably advances <see cref="BotSessionState.CurrentVer"/>
/// **before** any bot-observable side effect (the explicit
/// <c>FlushAsync</c> fence per RFC §4.8).
/// </summary>
public interface IUserBotSessionRegistry
{
    /// <summary>
    /// Returns the current state for <paramref name="credentialId"/>,
    /// allocating a fresh <c>(SessionId, CurrentVer=1)</c> tuple on first
    /// access and emitting a <c>BotSessionInitializedEvent</c> via the
    /// dispatcher so the allocation survives restart.
    /// </summary>
    Task<BotSessionState> GetOrCreateAsync(Guid credentialId, CancellationToken ct);

    /// <summary>
    /// Single-active-session enforcement. Returns <c>true</c> when the
    /// caller's <paramref name="connectionId"/> wins ownership of the
    /// session and <paramref name="attemptedVer"/> matches the current
    /// version. Returns <c>false</c> when another connection is already
    /// active OR when <paramref name="attemptedVer"/> is stale; the
    /// caller is responsible for invoking <see cref="BumpVersionAsync"/>
    /// per RFC §4.5 ("kick the squatter") in the in-use case.
    /// </summary>
    Task<bool> TryClaimActiveAsync(
        Guid credentialId,
        ulong attemptedVer,
        string connectionId,
        CancellationToken ct);

    /// <summary>
    /// Revalidates that the exact connection still owns the lease under the
    /// supplied session version. Used around EstablishAck/publication to close
    /// the claim-to-directory visibility gap during takeover.
    /// </summary>
    Task<bool> IsActiveLeaseAsync(
        Guid credentialId,
        ulong attemptedVer,
        string connectionId,
        CancellationToken ct);

    /// <summary>
    /// Releases the active-session slot when <paramref name="connectionId"/>
    /// matches the current owner. Called on Terminate or socket close so
    /// a subsequent reconnect can re-claim. Idempotent — releasing a slot
    /// already owned by a different connection (or none) is a no-op.
    /// </summary>
    Task ReleaseAsync(Guid credentialId, string connectionId, CancellationToken ct);

    /// <summary>
    /// Persists a <c>BotSessionVerAdvancedEvent</c>, mutates the in-memory
    /// state to <c>oldVer+1</c>, and **awaits an explicit
    /// <c>IEventStore.FlushAsync</c>** before returning — the durability
    /// fence required by RFC §4.8 so the next bot-observable response
    /// (typically an <c>EstablishReject(InvalidSessionVerId)</c>) cannot
    /// roll back across a crash. Returns the new (post-bump) version so
    /// the caller can echo it on the very reject that follows. The result also
    /// identifies the lease invalidated by the bump so the listener can close
    /// exactly that connection without racing a newer replacement.
    /// </summary>
    Task<BotSessionVersionAdvance> BumpVersionAsync(
        Guid credentialId,
        string reason,
        CancellationToken ct);

    /// <summary>
    /// Sub-issue #172 (F). Records a periodic checkpoint of the credential's
    /// outbound seq watermark — see RFC §4.8 cadence (5s OR 100 msgs). The
    /// implementation appends a <c>BotSessionSeqAdvancedEvent</c> via the
    /// dispatcher (best-effort durability — no FlushAsync) and updates the
    /// in-memory <see cref="BotSessionState.LastCheckpointedOutboundSeq"/>
    /// under the apply callback. No-op when <paramref name="checkpointedSeq"/>
    /// is <c>≤</c> the existing watermark (idempotent / reordering safe).
    /// </summary>
    void UpdateCheckpointedOutboundSeq(Guid credentialId, ulong checkpointedSeq);
}
