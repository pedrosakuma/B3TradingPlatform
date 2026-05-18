namespace B3.Trading.Api.Auth;

/// <summary>
/// User directory used by login + signup. Hides the split between
/// env-seeded users (immutable, from <see cref="AuthOptions.Users"/>)
/// and runtime users created via self-service signup.
///
/// <para>
/// v1 is in-memory only; runtime entries are lost on restart by design
/// (see plan: persistence is a tracked follow-up). Env-seeded users
/// always survive because they're rebuilt from configuration at boot.
/// </para>
/// </summary>
public interface IUserStore
{
    /// <summary>Lookup by username (case-insensitive).</summary>
    bool TryGet(string username, out UserConfig? user);

    /// <summary>
    /// Insert a runtime user. Returns <c>false</c> when the username
    /// already exists in either the env slot or the runtime slot.
    /// Thread-safe.
    /// </summary>
    bool TryAdd(UserConfig user);

    /// <summary>
    /// Persist mutations to an existing user (e.g. TOTP enrollment
    /// state). Returns <c>false</c> when the username is not known.
    /// Updates apply to both env-seeded and runtime users; for
    /// env-seeded users only the runtime-mutable fields (currently
    /// <see cref="UserConfig.Totp"/>) survive — config remains
    /// authoritative on restart for credential fields. Thread-safe.
    /// </summary>
    bool TryUpdate(UserConfig user);

    /// <summary>
    /// Atomically validates that <paramref name="matchedStep"/> is
    /// strictly greater than the user's persisted
    /// <see cref="UserTotpConfig.LastUsedTimeStep"/> and, if so,
    /// persists it. Returns <c>false</c> when the user is unknown, has
    /// no enrolled TOTP, or the step would be a same-window replay.
    /// On success, <paramref name="updatedUser"/> is the post-write
    /// snapshot. Implementations must serialize the read-modify-write
    /// under a per-user lock so two concurrent verifies racing on the
    /// same time step cannot both succeed.
    /// </summary>
    bool TryRecordTotpUse(string username, long matchedStep, out UserConfig? updatedUser);

    /// <summary>
    /// Atomically removes a single recovery-code hash from the user's
    /// <see cref="UserTotpConfig.RecoveryCodes"/> list. Returns a
    /// tri-state result so the caller can distinguish a genuinely
    /// wrong/typed code from a previously-valid code that has since
    /// been consumed (concurrent race winner already took it, or a
    /// straight replay after success). On <see cref="RecoveryCodeConsumeResult.Consumed"/>,
    /// <paramref name="updatedUser"/> is the post-write snapshot;
    /// otherwise it is <c>null</c>. Implementations must serialize
    /// the scan-remove-persist under a per-user lock so two concurrent
    /// verifies presenting the SAME recovery code cannot both succeed,
    /// and must record the consumed hash in
    /// <see cref="UserTotpConfig.ConsumedRecoveryCodes"/> as part of
    /// the same atomic write so the loser of a race observes
    /// <see cref="RecoveryCodeConsumeResult.AlreadyConsumed"/>.
    /// </summary>
    RecoveryCodeConsumeResult TryConsumeRecoveryCode(string username, string codeHash, out UserConfig? updatedUser);
}

/// <summary>
/// Outcome of <see cref="IUserStore.TryConsumeRecoveryCode"/>. The
/// distinction matters at the endpoint layer: a <see cref="NotFound"/>
/// is a wrong-code attempt (lockout counter must tick), while an
/// <see cref="AlreadyConsumed"/> is a benign race-loser or a
/// replay-after-success and MUST NOT increment the lockout counter
/// — otherwise a small concurrent burst (e.g. a user double-clicking
/// "Submit") could lock the account out instantly.
/// </summary>
public enum RecoveryCodeConsumeResult
{
    /// <summary>Hash matched an unused code and was consumed.</summary>
    Consumed,

    /// <summary>
    /// Hash was not in the unused list and has never been observed as
    /// consumed for this user. Treat as a genuine wrong-code attempt.
    /// </summary>
    NotFound,

    /// <summary>
    /// Hash was previously valid for this user but is no longer in the
    /// unused list — either a concurrent verify already won the race
    /// for the same code or the client is replaying a code that has
    /// already succeeded. Treat as a failed verify (401) but DO NOT
    /// increment the lockout counter.
    /// </summary>
    AlreadyConsumed,
}
