namespace B3.Trading.Infrastructure;

/// <summary>
/// Pass-1 review fix (#259, P1#5): when synthetic ER injection is
/// enabled (<c>Trading:Exchange:AllowErInjection=true</c>), refuse to
/// boot if the seeded admin user is still using the well-known
/// committed dev-default password material (the
/// <c>TRADING_SEED_PASSWORD_HASH</c> / <c>TRADING_SEED_PASSWORD_SALT</c>
/// pair from <c>docker/.env.example</c>, which corresponds to plaintext
/// <c>"wonderland"</c>).
///
/// <para>The conformance overlay <c>docker-compose.conformance.yml</c>
/// enables ER injection AND seeds an admin role; without this guard a
/// careless operator could bring up the conformance stack with the
/// committed defaults, which would expose
/// <c>POST /admin/simulator/er</c> to anyone with a copy of this repo.
/// The guard is pure-static so it can be unit-tested without spinning
/// up the host.</para>
/// </summary>
public static class AdminCredentialDefaultGuard
{
    /// <summary>
    /// The committed dev-default PBKDF2 hash from
    /// <c>docker/.env.example</c> (<c>TRADING_SEED_PASSWORD_HASH</c>).
    /// Plaintext is <c>"wonderland"</c>.
    /// </summary>
    public const string DevDefaultPasswordHash = "ZDzDHANAHq8NDQK3BWk/YZjybKLCMKdRzw0z9Da5wic=";

    /// <summary>
    /// The committed dev-default PBKDF2 salt from
    /// <c>docker/.env.example</c> (<c>TRADING_SEED_PASSWORD_SALT</c>).
    /// </summary>
    public const string DevDefaultPasswordSalt = "rXA+be7/gEYYZQrQDsUr2g==";

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when ER injection is
    /// enabled AND any seeded user with a non-default <c>role</c> (i.e.
    /// not the plain <c>"user"</c> role) carries the dev-default
    /// hash+salt pair. No-op otherwise.
    /// </summary>
    /// <param name="allowErInjection">The resolved value of <c>Trading:Exchange:AllowErInjection</c>.</param>
    /// <param name="seededUsers">Iterable of (role, passwordHash, salt) tuples — typically projected from <c>AuthOptions.Users</c>.</param>
    public static void Validate(bool allowErInjection, IEnumerable<(string Role, string PasswordHash, string Salt)> seededUsers)
    {
        if (!allowErInjection) return;
        if (seededUsers is null) return;

        foreach (var (role, hash, salt) in seededUsers)
        {
            if (string.IsNullOrEmpty(role) || string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(hash, DevDefaultPasswordHash, StringComparison.Ordinal)
                && string.Equals(salt, DevDefaultPasswordSalt, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Trading:Exchange:AllowErInjection=true is enabled AND a seeded user with role='{role}' is " +
                    "using the committed dev-default password material from docker/.env.example " +
                    "(TRADING_SEED_PASSWORD_HASH/_SALT, plaintext 'wonderland'). " +
                    "Refusing to boot — anyone with a copy of this repo could call POST /admin/simulator/er. " +
                    "Set B3T_CONFORMANCE_ADMIN_PASSWORD_HASH + B3T_CONFORMANCE_ADMIN_PASSWORD_SALT to a freshly " +
                    "generated PBKDF2 hash/salt pair before bringing up the conformance overlay.");
            }
        }
    }
}
