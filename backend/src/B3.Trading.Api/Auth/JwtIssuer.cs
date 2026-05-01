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

    public (string Token, DateTimeOffset ExpiresAt) Issue(string subject, string role)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_options.TokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(RoleClaim, role),
        };

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
