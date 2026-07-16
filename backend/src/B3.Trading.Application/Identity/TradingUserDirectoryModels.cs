namespace B3.Trading.Application.Identity;

public static class TradingUserDirectoryConstants
{
    public const int MaxTradingUserIdLength = 64;

    public const string RoleUser = "user";
    public const string RoleCompliance = "compliance";
    public const string RoleAdmin = "admin";

    public const string StatusActive = "active";
    public const string StatusDisabled = "disabled";

    public static bool IsValidRole(string role) =>
        string.Equals(role, RoleUser, StringComparison.Ordinal)
        || string.Equals(role, RoleCompliance, StringComparison.Ordinal)
        || string.Equals(role, RoleAdmin, StringComparison.Ordinal);

    public static bool IsValidStatus(string status) =>
        string.Equals(status, StatusActive, StringComparison.Ordinal)
        || string.Equals(status, StatusDisabled, StringComparison.Ordinal);
}

public sealed record TradingUser(
    string TradingUserId,
    string DisplayName,
    string FirmId,
    string Status,
    string Role,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExternalIdentityBinding> ExternalIdentities);

public sealed record ExternalIdentityBinding(
    long Id,
    string Issuer,
    string Subject,
    string TradingUserId,
    string? TenantId,
    string? ObjectId,
    DateTimeOffset CreatedAt);

public sealed record LegacyTradingUserImport(
    string TradingUserId,
    string DisplayName,
    string FirmId,
    string Role);

public sealed record ExternalIdentityBindingRequest(
    string Issuer,
    string Subject,
    string? TenantId = null,
    string? ObjectId = null);

public sealed record TradingUserDirectorySnapshot(
    IReadOnlyList<TradingUser> Users,
    int SchemaVersion);

public sealed record TradingUserDirectoryBackup(
    string Path,
    int SchemaVersion,
    DateTimeOffset CreatedAt);

public sealed record TradingUserDirectoryHealth(
    bool Ready,
    string Provider,
    string? Path,
    int? SchemaVersion,
    string? Reason);

public class TradingUserDirectoryException : Exception
{
    public TradingUserDirectoryException(string message) : base(message) { }
    public TradingUserDirectoryException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class TradingUserDirectoryValidationException : TradingUserDirectoryException
{
    public TradingUserDirectoryValidationException(string message) : base(message) { }
}

public sealed class TradingUserDirectoryConflictException : TradingUserDirectoryException
{
    public TradingUserDirectoryConflictException(string message) : base(message) { }
}

public sealed class TradingUserDirectoryConcurrencyException : TradingUserDirectoryException
{
    public TradingUserDirectoryConcurrencyException(string message) : base(message) { }
}

public sealed class TradingUserDirectoryUnavailableException : TradingUserDirectoryException
{
    public TradingUserDirectoryUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    public TradingUserDirectoryUnavailableException(string message) : base(message) { }
}

public sealed class TradingUserDirectoryUnsupportedSchemaException : TradingUserDirectoryException
{
    public TradingUserDirectoryUnsupportedSchemaException(int actual, int supported)
        : base($"Identity directory schema version {actual} is newer than supported version {supported}.")
    {
        ActualVersion = actual;
        SupportedVersion = supported;
    }

    public int ActualVersion { get; }
    public int SupportedVersion { get; }
}
