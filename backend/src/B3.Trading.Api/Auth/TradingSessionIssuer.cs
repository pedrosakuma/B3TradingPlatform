using System.Security.Claims;
using B3.Trading.Application;
using B3.Trading.Application.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

public interface ITradingSessionIssuer
{
    Task<TradingSessionIssueResult> IssueForLocalUserAsync(UserConfig user, CancellationToken ct = default);
    TradingSessionIssueResult IssueForExternalUser(TradingUser user);
}

public sealed record TradingSessionIssueResult(
    bool Succeeded,
    string? ErrorCode,
    int StatusCode,
    string? Token,
    DateTimeOffset? ExpiresAt,
    string? TradingUserId,
    string? Firm,
    string? Role)
{
    public static TradingSessionIssueResult Success(
        string token,
        DateTimeOffset expiresAt,
        string tradingUserId,
        string firm,
        string role) =>
        new(true, null, StatusCodes.Status200OK, token, expiresAt, tradingUserId, firm, role);

    public static TradingSessionIssueResult Failure(string code, int statusCode) =>
        new(false, code, statusCode, null, null, null, null, null);
}

internal sealed class TradingSessionIssuer : ITradingSessionIssuer
{
    public const string EntraExchangeAmr = "entra_exchange";

    private readonly AuthOptions _auth;
    private readonly ITradingUserDirectory _directory;
    private readonly JwtIssuer _jwtIssuer;
    private readonly EndClientRegistry _registry;

    public TradingSessionIssuer(
        IOptions<AuthOptions> auth,
        ITradingUserDirectory directory,
        JwtIssuer jwtIssuer,
        EndClientRegistry registry)
    {
        _auth = auth.Value;
        _directory = directory;
        _jwtIssuer = jwtIssuer;
        _registry = registry;
    }

    public async Task<TradingSessionIssueResult> IssueForLocalUserAsync(UserConfig user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_auth.ResolveMode() == AuthModeKind.Local)
            return IssueLocalCompatibility(user);

        TradingUser? directoryUser;
        try
        {
            directoryUser = await _directory.GetUserAsync(user.Username, ct);
        }
        catch (TradingUserDirectoryException)
        {
            return TradingSessionIssueResult.Failure("identity_directory_unavailable", StatusCodes.Status503ServiceUnavailable);
        }

        return directoryUser is null
            ? TradingSessionIssueResult.Failure("account_not_provisioned", StatusCodes.Status403Forbidden)
            : IssueDirectoryBacked(directoryUser, amr: null);
    }

    public TradingSessionIssueResult IssueForExternalUser(TradingUser user) =>
        IssueDirectoryBacked(user, EntraExchangeAmr);

    private TradingSessionIssueResult IssueLocalCompatibility(UserConfig user)
    {
        _registry.Register(user.Username);
        var (token, expires) = _jwtIssuer.Issue(user.Username, user.Role, user.Firm);
        return TradingSessionIssueResult.Success(token, expires, user.Username, user.Firm, user.Role);
    }

    private TradingSessionIssueResult IssueDirectoryBacked(TradingUser user, string? amr)
    {
        if (!string.Equals(user.Status, TradingUserDirectoryConstants.StatusActive, StringComparison.Ordinal))
            return TradingSessionIssueResult.Failure("account_disabled", StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(user.FirmId) || !TradingUserDirectoryConstants.IsValidRole(user.Role))
            return TradingSessionIssueResult.Failure("account_incomplete", StatusCodes.Status403Forbidden);

        _registry.Register(user.TradingUserId);
        var additional = amr is null ? null : new[] { new Claim("amr", amr) };
        var (token, expires) = _jwtIssuer.Issue(
            user.TradingUserId,
            user.Role,
            user.FirmId,
            _auth.ExternalIdentity.InternalTokenLifetime,
            additional);
        return TradingSessionIssueResult.Success(token, expires, user.TradingUserId, user.FirmId, user.Role);
    }
}
