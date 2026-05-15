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
}
