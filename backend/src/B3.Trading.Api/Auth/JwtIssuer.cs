using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace B3.Trading.Api.Auth;

/// <summary>
/// Issues HS256-signed JWTs against <see cref="AuthOptions"/>. The signing
/// key is validated to be at least 32 bytes (256 bits) at startup; weaker
/// configurations fail fast.
/// </summary>
public sealed class JwtIssuer
{
    public const string RoleClaim = "role";
    public const string FirmClaim = "firm";

    private readonly AuthOptions _options;
    private readonly SigningCredentials _credentials;

    public JwtIssuer(IOptions<AuthOptions> options)
    {
        _options = options.Value;
        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey ?? string.Empty);
        if (keyBytes.Length < 32)
            throw new InvalidOperationException(
                $"{AuthOptions.SectionName}:SigningKey must be at least 32 bytes (256 bits).");
        _credentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(string subject, string role, string firm = "default")
        => Issue(subject, role, firm, TimeSpan.FromMinutes(_options.TokenLifetimeMinutes), additionalClaims: null, includeIssuedAt: false);

    public (string Token, DateTimeOffset ExpiresAt) Issue(
        string subject,
        string role,
        string firm,
        TimeSpan lifetime,
        IEnumerable<Claim>? additionalClaims = null)
        => Issue(subject, role, firm, lifetime, additionalClaims, includeIssuedAt: true);

    private (string Token, DateTimeOffset ExpiresAt) Issue(
        string subject,
        string role,
        string firm,
        TimeSpan lifetime,
        IEnumerable<Claim>? additionalClaims,
        bool includeIssuedAt)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(RoleClaim, role),
            new(FirmClaim, firm),
        };
        if (includeIssuedAt)
        {
            claims.Add(new Claim(
                JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));
        }
        if (additionalClaims is not null)
            claims.AddRange(additionalClaims);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
