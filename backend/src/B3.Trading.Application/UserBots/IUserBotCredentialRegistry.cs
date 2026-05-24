namespace B3.Trading.Application.UserBots;

/// <summary>
/// Storage and lifecycle surface for user-issued bot credentials
/// (sub-issue #169 of RFC user-bot-fixp-listener-v0). The interface
/// keeps the read-side stable for the future FIXP listener
/// (sub-issue D) which only ever calls <see cref="TryAuthenticateAsync"/>.
/// </summary>
public interface IUserBotCredentialRegistry
{
    /// <summary>
    /// Mints a new PAT for <paramref name="userId"/>, persists the
    /// bcrypt hash + metadata to the WAL, and returns the plaintext
    /// secret exactly once. Caller must surface the secret to the
    /// human user and not log it.
    /// </summary>
    Task<CreatedUserBotCredential> CreateAsync(
        string userId,
        string label,
        CancellationToken ct,
        string firmId = "default");

    /// <summary>
    /// Soft-revoke (sets <c>RevokedAtUtc</c>). Returns <c>false</c>
    /// when the credential id does not exist OR is already revoked OR
    /// belongs to a different user — the caller cannot distinguish
    /// these cases (404 either way) so a cross-user probe leaks
    /// nothing.
    /// </summary>
    Task<bool> RevokeAsync(string userId, Guid credentialId, CancellationToken ct);

    /// <summary>
    /// All credentials minted by <paramref name="userId"/>, including
    /// revoked ones (the UI shows a strike-through audit trail). Never
    /// includes the plaintext secret — only the public metadata.
    /// </summary>
    IReadOnlyList<UserBotCredential> ListByUser(string userId);

    /// <summary>
    /// Resolves the PAT presented by an incoming FIXP Negotiate (or any
    /// other authenticator) back to the registered credential. Returns
    /// <c>null</c> when the token is malformed, the short-id is unknown,
    /// the credential is revoked, or the secret half does not bcrypt-verify.
    /// Sub-issue D will call this from <c>EntryPointListener.HandleNegotiate</c>.
    /// </summary>
    Task<UserBotCredential?> TryAuthenticateAsync(string presentedToken, CancellationToken ct);
}
