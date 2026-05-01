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
