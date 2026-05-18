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
    /// <see cref="UserTotpConfig.RecoveryCodes"/> list. Returns
    /// <c>false</c> when the user is unknown, has no enrolled TOTP, or
    /// the hash is not present (already consumed by a racing request,
    /// or simply wrong). On success, <paramref name="updatedUser"/> is
    /// the post-write snapshot. Implementations must serialize the
    /// scan-remove-persist under a per-user lock so two concurrent
    /// verifies presenting the SAME recovery code cannot both succeed.
    /// </summary>
    bool TryConsumeRecoveryCode(string username, string codeHash, out UserConfig? updatedUser);
}
