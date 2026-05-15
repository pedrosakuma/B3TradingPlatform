using B3.Trading.Infrastructure;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Pass-1 review fix coverage for <see cref="AdminCredentialDefaultGuard"/>
/// (#259, P1#5). Validates the conformance-overlay safeguard that
/// refuses to boot when synthetic ER injection is enabled and a seeded
/// non-user role still carries the committed dev-default password
/// material from <c>docker/.env.example</c>.
/// </summary>
public class AdminCredentialDefaultGuardTests
{
    private const string DevHash = AdminCredentialDefaultGuard.DevDefaultPasswordHash;
    private const string DevSalt = AdminCredentialDefaultGuard.DevDefaultPasswordSalt;
    private const string OtherHash = "ZmFrZS1ub24tZGVmYXVsdC1oYXNoLXZhbHVl";
    private const string OtherSalt = "ZmFrZS1zYWx0LXZhbHVl";

    [Fact]
    public void Validate_AllowErInjectionFalse_DoesNotThrow_EvenWithDefaultAdmin()
    {
        AdminCredentialDefaultGuard.Validate(
            allowErInjection: false,
            seededUsers: new[] { ("admin", DevHash, DevSalt) });
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_AdminWithDevDefaults_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdminCredentialDefaultGuard.Validate(
                allowErInjection: true,
                seededUsers: new[] { ("admin", DevHash, DevSalt) }));
        Assert.Contains("AllowErInjection=true", ex.Message);
        Assert.Contains("dev-default", ex.Message);
        Assert.Contains("B3T_CONFORMANCE_ADMIN_PASSWORD_HASH", ex.Message);
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_AdminWithFreshCredentials_DoesNotThrow()
    {
        AdminCredentialDefaultGuard.Validate(
            allowErInjection: true,
            seededUsers: new[] { ("admin", OtherHash, OtherSalt) });
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_OnlyUserRoleHasDefaults_DoesNotThrow()
    {
        // The plain `user` role is allowed to keep the dev defaults
        // because it cannot reach POST /admin/simulator/er.
        AdminCredentialDefaultGuard.Validate(
            allowErInjection: true,
            seededUsers: new[]
            {
                ("user", DevHash, DevSalt),
                ("admin", OtherHash, OtherSalt),
            });
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_NonAdminPrivilegedRoleWithDefaults_Throws()
    {
        // Any role !"user" is treated as privileged for the purpose of
        // this guard — future role names (e.g. "ops", "support") get
        // the same protection by default.
        Assert.Throws<InvalidOperationException>(() =>
            AdminCredentialDefaultGuard.Validate(
                allowErInjection: true,
                seededUsers: new[] { ("ops", DevHash, DevSalt) }));
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_HashMatchesButSaltDiffers_DoesNotThrow()
    {
        // Both fields must match the committed defaults; a partial
        // match (e.g. a fresh salt) means the operator has rotated.
        AdminCredentialDefaultGuard.Validate(
            allowErInjection: true,
            seededUsers: new[] { ("admin", DevHash, OtherSalt) });
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_NullSeededUsers_DoesNotThrow()
    {
        AdminCredentialDefaultGuard.Validate(allowErInjection: true, seededUsers: null!);
    }

    // ----------------------------------------------------------------
    // Pass-2 review fix (#259, P1) — whitespace-equivalent Base64
    // bypass. PasswordHasher uses Convert.FromBase64String which silently
    // ignores whitespace; the pre-fix guard's ordinal string compare let
    // a whitespace-padded copy of the dev default through. The guard
    // now decodes both sides and compares bytes.
    // ----------------------------------------------------------------

    [Fact]
    public void Validate_AllowErInjectionTrue_DefaultHashWithEmbeddedNewline_StillTrips()
    {
        // Inject a CRLF + spaces in the middle of both base64 strings.
        // Convert.FromBase64String happily decodes these to the SAME
        // bytes as the unmodified defaults, so the auth layer would
        // accept them as "wonderland" — the guard MUST too.
        var hashWithWhitespace = DevHash[..10] + "\r\n  " + DevHash[10..];
        var saltWithWhitespace = DevSalt[..6] + "\n\t" + DevSalt[6..];

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdminCredentialDefaultGuard.Validate(
                allowErInjection: true,
                seededUsers: new[] { ("admin", hashWithWhitespace, saltWithWhitespace) }));
        Assert.Contains("dev-default", ex.Message);
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_DifferentHashSameDecodedLength_DoesNotTrip()
    {
        // Distinct 32-byte hash and 16-byte salt — same shape as the
        // PBKDF2 defaults but with different bytes. Must NOT trip.
        var differentHash = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
        var differentSalt = Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)(i + 100)).ToArray());

        AdminCredentialDefaultGuard.Validate(
            allowErInjection: true,
            seededUsers: new[] { ("admin", differentHash, differentSalt) });
    }

    [Fact]
    public void Validate_AllowErInjectionTrue_MalformedBase64_DoesNotTrip()
    {
        // Garbage that won't decode. Defer to login-time validation,
        // don't throw a misleading "you're using the dev default!" here.
        AdminCredentialDefaultGuard.Validate(
            allowErInjection: true,
            seededUsers: new[] { ("admin", "@@@not-base64!!!", "###also-not###") });
    }
}
