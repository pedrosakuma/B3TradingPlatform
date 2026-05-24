namespace B3.Trading.Application.UserBots;

/// <summary>
/// Persistent registration of a user-issued bot credential
/// (RFC user-bot-fixp-listener-v0 §4.5, sub-issue #169).
/// The plaintext PAT secret is shown to the caller exactly once at
/// creation time and is never retained by the platform — only the
/// bcrypt(cost=12) <see cref="SecretHash"/> is persisted to the WAL
/// and snapshot store. <see cref="CredShortId"/> is the public
/// identifier embedded in the PAT (<c>b3t_&lt;shortId&gt;_&lt;secret&gt;</c>)
/// so the FIXP listener (sub-issue D) can locate the matching record
/// in O(1) without scanning every credential.
/// </summary>
/// <remarks>
/// <see cref="UserId"/> is the JWT <c>sub</c> claim of the human user
/// that created this credential. The wider RFC text refers to a
/// <c>UserId</c> "Guid" but the platform has no Guid user identifier;
/// the <c>sub</c>-string convention is the same one used by every
/// other per-user surface (cash ledger, positions, kill-switch).
/// Sub-issue D's listener must look up the human owner via this field.
/// </remarks>
/// <param name="FirmId">
/// Firm scope inherited from the JWT <c>firm</c> claim of the human user
/// at credential creation time (#431). Snapshots / WAL events minted by
/// older builds replay with the legacy <c>"default"</c> sentinel so the
/// pre-existing single-firm deployments keep their attribution.
/// </param>
public sealed record UserBotCredential(
    Guid Id,
    string UserId,
    string CredShortId,
    string Label,
    string SecretHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc = null,
    string FirmId = "default");

/// <summary>
/// One-shot DTO returned by <see cref="IUserBotCredentialRegistry.CreateAsync"/>.
/// Carries the plaintext PAT (<see cref="PlainToken"/>) for the caller
/// to display once. Subsequent reads via <c>ListByUser</c> never expose
/// this field — the registry only stores the bcrypt hash.
/// </summary>
public sealed record CreatedUserBotCredential(
    UserBotCredential Credential,
    string PlainToken);
