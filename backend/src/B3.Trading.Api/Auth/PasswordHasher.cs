using System.Security.Cryptography;
using System.Text;

namespace B3.Trading.Api.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing. Iteration count is per-user (stored
/// alongside the hash) so existing users are unaffected when the platform
/// default moves up. Hash + salt are base64-encoded.
/// </summary>
public static class PasswordHasher
{
    private const int HashBytes = 32;
    private const int SaltBytes = 16;

    public static (string HashB64, string SaltB64) Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string expectedHashB64, string saltB64, int iterations)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(expectedHashB64) || string.IsNullOrEmpty(saltB64))
            return false;

        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(expectedHashB64);
            salt = Convert.FromBase64String(saltB64);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
