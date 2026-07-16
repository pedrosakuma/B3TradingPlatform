namespace B3.Trading.Application.Identity;

public interface ITradingUserDirectory
{
    string ProviderName { get; }
    string? StorePath { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task<TradingUserDirectoryHealth> CheckHealthAsync(CancellationToken ct = default);

    Task<TradingUser?> GetUserAsync(string tradingUserId, CancellationToken ct = default);

    Task<TradingUser?> ResolveExternalIdentityAsync(
        string issuer,
        string subject,
        CancellationToken ct = default);

    Task<IReadOnlyList<TradingUser>> ListUsersAsync(CancellationToken ct = default);

    Task<bool> HasActiveExternallyLinkedAdminAsync(CancellationToken ct = default);

    Task<int> ImportLegacyUsersAsync(
        IReadOnlyCollection<LegacyTradingUserImport> users,
        CancellationToken ct = default);

    Task<ExternalIdentityBinding> BindExternalIdentityAsync(
        string tradingUserId,
        ExternalIdentityBindingRequest binding,
        long expectedRowVersion,
        CancellationToken ct = default);

    Task UnbindExternalIdentityAsync(
        string tradingUserId,
        long bindingId,
        long expectedRowVersion,
        CancellationToken ct = default);

    Task SetStatusAsync(
        string tradingUserId,
        string status,
        long expectedRowVersion,
        CancellationToken ct = default);

    Task SetFirmAndRoleAsync(
        string tradingUserId,
        string firmId,
        string role,
        long expectedRowVersion,
        CancellationToken ct = default);

    Task<RecoveryAdminResult> EnsureRecoveryAdminAsync(
        RecoveryAdminRequest request,
        CancellationToken ct = default);

    Task<TradingUserDirectoryBackup> CreateBackupAsync(
        string destinationPath,
        CancellationToken ct = default);
}
