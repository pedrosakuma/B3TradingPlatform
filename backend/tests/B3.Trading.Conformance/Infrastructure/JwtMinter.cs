using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace B3.Trading.Conformance.Infrastructure;

/// <summary>
/// Mints HS256-signed JWTs deterministically against the host's signing
/// key (env <c>B3T_AUTH_SIGNING_KEY</c>). Used by scenarios that need a
/// token with custom claims/expiry — most notably "expired-but-otherwise-
/// authentic JWT must be rejected", which can only be expressed when the
/// suite is signing with the same key the host validates with.
/// </summary>
/// <remarks>
/// Mirrors <c>B3.Trading.Api.Auth.JwtIssuer</c> on purpose: same algorithm,
/// same default issuer/audience. Diverging here would just produce
/// false-positive 401s and obscure what the spec is actually asserting.
/// </remarks>
internal static class JwtMinter
{
    public const string RoleClaim = "role";
    public const string FirmClaim = "firm";

    public static string Mint(
        string subject,
        string role,
        string firm,
        DateTimeOffset notBefore,
        DateTimeOffset expires)
    {
        var keyBytes = Encoding.UTF8.GetBytes(PlatformEndpoint.GetAuthSigningKey());
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"{PlatformEndpoint.EnvAuthSigningKey} must be at least 32 bytes (256 bits) — must mirror the host's Trading:Auth:SigningKey.");
        }
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(RoleClaim, role),
            new(FirmClaim, firm),
        };

        var token = new JwtSecurityToken(
            issuer: PlatformEndpoint.GetAuthIssuer(),
            audience: PlatformEndpoint.GetAuthAudience(),
            claims: claims,
            notBefore: notBefore.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Convenience: mint a token whose <c>exp</c> claim is already in the
    /// past. The returned token is signature-valid; the host must reject
    /// it on the lifetime check, not on the signature check.
    /// </summary>
    public static string MintExpired(
        string subject = "conformance-expired",
        string role = "user",
        string firm = "default")
    {
        var now = DateTimeOffset.UtcNow;
        // Generous lifetime that still ends in the past, comfortably outside
        // any clock-skew tolerance the JWT bearer middleware might apply
        // (default 5 minutes in Microsoft.IdentityModel).
        return Mint(subject, role, firm,
            notBefore: now.AddHours(-2),
            expires: now.AddHours(-1));
    }
}
