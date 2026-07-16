using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace B3.Trading.Api.Auth;

public interface IExternalIdentityTokenValidator
{
    Task<ExternalIdentityValidationResult> ValidateAsync(string bearerToken, CancellationToken ct = default);
}

public interface IExternalIdentityConfigurationProvider
{
    Task<ExternalIdentityConfiguration> GetConfigurationAsync(CancellationToken ct = default);
    void RequestRefresh();
}

public sealed record ExternalIdentityConfiguration(
    string Issuer,
    IReadOnlyCollection<SecurityKey> SigningKeys);

public sealed record ExternalIdentityValidationResult(
    ExternalIdentityValidationStatus Status,
    string Code,
    string? Issuer = null,
    string? Subject = null,
    string? TenantId = null,
    string? ObjectId = null);

public enum ExternalIdentityValidationStatus
{
    Success,
    InvalidToken,
    IdentityProviderUnavailable,
}

internal sealed class ExternalIdentityTokenValidator : IExternalIdentityTokenValidator
{
    private static readonly string[] ValidAlgorithms = { SecurityAlgorithms.RsaSha256 };

    private readonly AuthOptions _auth;
    private readonly IExternalIdentityConfigurationProvider _configurationProvider;
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public ExternalIdentityTokenValidator(
        IOptions<AuthOptions> auth,
        IExternalIdentityConfigurationProvider configurationProvider)
    {
        _auth = auth.Value;
        _configurationProvider = configurationProvider;
    }

    public async Task<ExternalIdentityValidationResult> ValidateAsync(string bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return Invalid();

        ExternalIdentityConfiguration configuration;
        try
        {
            configuration = await _configurationProvider.GetConfigurationAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Unavailable();
        }

        if (!string.Equals(configuration.Issuer, _auth.ExternalIdentity.Issuer, StringComparison.Ordinal))
            return Unavailable();

        var validation = TryValidateToken(bearerToken, configuration, out var principal, out var token);
        if (validation == TokenValidationAttempt.KeyNotFound)
        {
            _configurationProvider.RequestRefresh();
            try
            {
                configuration = await _configurationProvider.GetConfigurationAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Unavailable();
            }

            validation = TryValidateToken(bearerToken, configuration, out principal, out token);
            if (validation == TokenValidationAttempt.KeyNotFound)
                return Invalid();
        }

        if (validation == TokenValidationAttempt.Invalid || principal is null || token is null)
            return Invalid();

        if (!string.Equals(token.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            return Invalid();

        var claimsValidation = ValidateClaims(principal);
        return claimsValidation ?? new ExternalIdentityValidationResult(
            ExternalIdentityValidationStatus.Success,
            "ok",
            Issuer: principal.FindFirstValue(JwtRegisteredClaimNames.Iss),
            Subject: principal.FindFirstValue(JwtRegisteredClaimNames.Sub),
            TenantId: principal.FindFirstValue("tid"),
            ObjectId: principal.FindFirstValue("oid"));
    }

    private TokenValidationAttempt TryValidateToken(
        string token,
        ExternalIdentityConfiguration configuration,
        out ClaimsPrincipal? principal,
        out JwtSecurityToken? jwt)
    {
        principal = null;
        jwt = null;
        try
        {
            principal = _handler.ValidateToken(token, BuildParameters(configuration), out var validatedToken);
            jwt = validatedToken as JwtSecurityToken;
            return jwt is null ? TokenValidationAttempt.Invalid : TokenValidationAttempt.Success;
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            return TokenValidationAttempt.KeyNotFound;
        }
        catch (SecurityTokenException)
        {
            return TokenValidationAttempt.Invalid;
        }
        catch (ArgumentException)
        {
            return TokenValidationAttempt.Invalid;
        }
    }

    private TokenValidationParameters BuildParameters(ExternalIdentityConfiguration configuration) =>
        new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidIssuer = _auth.ExternalIdentity.Issuer,
            ValidAudience = _auth.ExternalIdentity.Audience,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = ValidAlgorithms,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = JwtIssuer.RoleClaim,
        };

    private ExternalIdentityValidationResult? ValidateClaims(ClaimsPrincipal principal)
    {
        if (!string.Equals(principal.FindFirstValue("ver"), "2.0", StringComparison.Ordinal))
            return Invalid();

        var issuer = principal.FindFirstValue(JwtRegisteredClaimNames.Iss);
        if (!string.Equals(issuer, _auth.ExternalIdentity.Issuer, StringComparison.Ordinal))
            return Invalid();

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(subject))
            return Invalid();

        if (_auth.ExternalIdentity.RequireTenantId
            && !string.Equals(principal.FindFirstValue("tid"), _auth.ExternalIdentity.TenantId, StringComparison.Ordinal))
            return Invalid();

        var scope = principal.FindFirstValue("scp");
        if (!ContainsScope(scope, _auth.ExternalIdentity.RequiredScope))
            return Invalid();

        var azp = principal.FindFirstValue("azp");
        if (string.IsNullOrEmpty(azp)
            || !_auth.ExternalIdentity.AllowedClientApplicationIds.Contains(azp, StringComparer.Ordinal))
            return Invalid();

        var azpacr = principal.FindFirstValue("azpacr");
        if (azpacr is not null && !string.Equals(azpacr, "0", StringComparison.Ordinal))
            return Invalid();

        return null;
    }

    private static bool ContainsScope(string? scopes, string requiredScope)
    {
        if (string.IsNullOrWhiteSpace(scopes) || string.IsNullOrWhiteSpace(requiredScope))
            return false;
        return scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(requiredScope, StringComparer.Ordinal);
    }

    private static ExternalIdentityValidationResult Invalid() =>
        new(ExternalIdentityValidationStatus.InvalidToken, "invalid_external_token");

    private static ExternalIdentityValidationResult Unavailable() =>
        new(ExternalIdentityValidationStatus.IdentityProviderUnavailable, "identity_provider_unavailable");

    private enum TokenValidationAttempt
    {
        Success,
        Invalid,
        KeyNotFound,
    }
}
