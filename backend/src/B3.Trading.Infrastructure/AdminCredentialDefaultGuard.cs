using System.Security.Cryptography;

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
/// <c>POST /api/admin/simulator/er</c> to anyone with a copy of this repo.
/// The guard is pure-static so it can be unit-tested without spinning
/// up the host.</para>
///
/// <para>Pass-2 review fix (#259, P1): the comparison is performed on
/// the DECODED bytes (<see cref="Convert.FromBase64String"/>) of both
/// the configured and dev-default hash/salt, using
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
/// <c>Convert.FromBase64String</c> ignores embedded whitespace, so
/// <c>PasswordHasher</c> would happily accept
/// <c>"ZDzDHANAHq8N\nDQK3BWk/YZjybKLCMKdRzw0z9Da5wic="</c> as the same
/// secret — but a naive ordinal string compare would let it slip past
/// this guard. Decoding both sides closes that bypass and applies the
/// check to BOTH hash and salt fields. If a configured value is not
/// valid Base64, it is treated as not-default (login-time validation
/// will reject it on its own).</para>
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

    private static readonly byte[] DevDefaultPasswordHashBytes = Convert.FromBase64String(DevDefaultPasswordHash);
    private static readonly byte[] DevDefaultPasswordSaltBytes = Convert.FromBase64String(DevDefaultPasswordSalt);

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
            if (DecodedEquals(hash, DevDefaultPasswordHashBytes)
                && DecodedEquals(salt, DevDefaultPasswordSaltBytes))
            {
                throw new InvalidOperationException(
                    $"Trading:Exchange:AllowErInjection=true is enabled AND a seeded user with role='{role}' is " +
                    "using the committed dev-default password material from docker/.env.example " +
                    "(TRADING_SEED_PASSWORD_HASH/_SALT, plaintext 'wonderland'). " +
                    "Refusing to boot — anyone with a copy of this repo could call POST /api/admin/simulator/er. " +
                    "Set B3T_CONFORMANCE_ADMIN_PASSWORD_HASH + B3T_CONFORMANCE_ADMIN_PASSWORD_SALT to a freshly " +
                    "generated PBKDF2 hash/salt pair before bringing up the conformance overlay.");
            }
        }
    }

    /// <summary>
    /// Decode <paramref name="configured"/> as Base64 and compare the
    /// resulting bytes to <paramref name="defaultBytes"/> with a
    /// constant-time check. Returns <c>false</c> on null/empty input or
    /// any <see cref="FormatException"/>: malformed Base64 in the
    /// configured slot is left for login-time validation rather than
    /// generating a false positive here.
    /// </summary>
    private static bool DecodedEquals(string? configured, byte[] defaultBytes)
    {
        if (string.IsNullOrEmpty(configured)) return false;
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(decoded, defaultBytes);
    }
}
