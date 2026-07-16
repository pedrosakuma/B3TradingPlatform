using B3.Trading.Api.Auth;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Verify_AcceptsCorrectPassword()
    {
        var (hash, salt) = PasswordHasher.Hash("hunter2", 10_000);
        Assert.True(PasswordHasher.Verify("hunter2", hash, salt, 10_000));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var (hash, salt) = PasswordHasher.Hash("hunter2", 10_000);
        Assert.False(PasswordHasher.Verify("wrong", hash, salt, 10_000));
    }

    [Fact]
    public void Verify_RejectsTamperedHash()
    {
        var (hash, salt) = PasswordHasher.Hash("hunter2", 10_000);
        var tampered = hash[..^4] + "AAAA";
        Assert.False(PasswordHasher.Verify("hunter2", tampered, salt, 10_000));
    }

    [Fact]
    public void Verify_HandlesGarbageInput()
    {
        Assert.False(PasswordHasher.Verify("x", "not-base64!!!", "neither", 10_000));
        Assert.False(PasswordHasher.Verify("", "AAAA", "AAAA", 10_000));
    }
}

public class JwtIssuerTests
{
    [Fact]
    public void Constructor_RejectsShortSigningKey()
    {
        var opts = Options.Create(new AuthOptions { SigningKey = "too-short" });
        Assert.Throws<InvalidOperationException>(() => new JwtIssuer(opts));
    }

    [Fact]
    public void Issue_ProducesParseableJwtWithSubAndRole()
    {
        var opts = Options.Create(new AuthOptions
        {
            SigningKey = "test-signing-key-must-be-at-least-32-bytes-long-okay",
            TokenLifetimeMinutes = 5,
        });
        var issuer = new JwtIssuer(opts);

        var (token, expires) = issuer.Issue("alice", "user");

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(expires > DateTimeOffset.UtcNow);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        Assert.Equal("alice", jwt.Subject);
        Assert.Contains(jwt.Claims, c => c.Type == JwtIssuer.RoleClaim && c.Value == "user");
    }
}

public class AuthOptionsValidatorTests
{
    [Fact]
    public void Validate_EntraRejectsLocalEndpoints()
    {
        var opts = ValidExternalOptions();
        opts.Mode = AuthModes.Entra;
        opts.LocalLoginEnabled = true;

        var result = new AuthOptionsValidator().Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("LocalLoginEnabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_HybridRequiresExternalIdentity()
    {
        var opts = new AuthOptions { Mode = AuthModes.Hybrid };

        var result = new AuthOptionsValidator().Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("ExternalIdentity:Issuer", StringComparison.Ordinal));
    }

    private static AuthOptions ValidExternalOptions() => new()
    {
        SigningKey = "test-signing-key-must-be-at-least-32-bytes-long-okay",
        Mode = AuthModes.Hybrid,
        ExternalIdentity = new ExternalIdentityOptions
        {
            Authority = "https://tenant.ciamlogin.com/tenant/v2.0",
            Issuer = "https://tenant.ciamlogin.com/tenant/v2.0",
            TenantId = "tenant",
            Audience = "api://trading",
            RequiredScope = "Trading.Access",
            AllowedClientApplicationIds = new() { "spa" },
        },
    };
}
