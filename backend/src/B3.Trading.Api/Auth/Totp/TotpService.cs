using System.Security.Cryptography;
using System.Text;
using OtpNet;

namespace B3.Trading.Api.Auth.Totp;

/// <summary>
/// Pure helpers for generating, encoding and verifying TOTP material
/// per RFC 6238 (SHA-1, 6 digits, 30-second step). Wraps Otp.NET so
/// the rest of the codebase has a single seam.
/// </summary>
public interface ITotpService
{
    /// <summary>Generate a fresh 20-byte (160-bit) base32 shared secret.</summary>
    string GenerateBase32Secret();

    /// <summary>
    /// Build the standard otpauth URI consumed by Google Authenticator,
    /// 1Password, Authy, etc.
    /// </summary>
    string BuildOtpAuthUri(string issuer, string username, string base32Secret);

    /// <summary>
    /// Verifies <paramref name="code"/> against <paramref name="base32Secret"/>.
    /// Accepts the previous + current + next step (±1 window) so a code
    /// generated at second 29 is still valid when received at second 30.
    /// </summary>
    bool Verify(string base32Secret, string code);

    /// <summary>Generate <paramref name="count"/> human-friendly recovery codes.</summary>
    IReadOnlyList<string> GenerateRecoveryCodes(int count);

    /// <summary>One-way hash used to store / compare recovery codes.</summary>
    string HashRecoveryCode(string code);
}

internal sealed class TotpService : ITotpService
{
    // 20 bytes (160 bits) is the SHA-1 RFC 4226 recommendation.
    private const int SecretByteLength = 20;

    public string GenerateBase32Secret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretByteLength);
        return Base32Encoding.ToString(bytes).TrimEnd('=');
    }

    public string BuildOtpAuthUri(string issuer, string username, string base32Secret)
    {
        var encIssuer = Uri.EscapeDataString(issuer);
        var encUser = Uri.EscapeDataString(username);
        // Label is "Issuer:User" so authenticator apps show it grouped
        // under the issuer; query string repeats issuer per the de-facto
        // Google Authenticator extension.
        return $"otpauth://totp/{encIssuer}:{encUser}?secret={base32Secret}&issuer={encIssuer}&algorithm=SHA1&digits=6&period=30";
    }

    public bool Verify(string base32Secret, string code)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
            return false;

        // Strip whitespace so users pasting "123 456" from authenticator
        // apps don't get a needless rejection.
        var trimmed = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (trimmed.Length != 6 || !trimmed.All(char.IsDigit)) return false;

        byte[] secret;
        try { secret = Base32Encoding.ToBytes(base32Secret); }
        catch (ArgumentException) { return false; }

        var totp = new OtpNet.Totp(secret);
        return totp.VerifyTotp(trimmed, out _, new VerificationWindow(previous: 1, future: 1));
    }

    public IReadOnlyList<string> GenerateRecoveryCodes(int count)
    {
        if (count <= 0) return Array.Empty<string>();
        // 10 chars from a 32-symbol alphabet (Crockford-ish, no I/O/0/1
        // to avoid confusion). Formatted "XXXXX-XXXXX" for legibility.
        // 32^10 ≈ 1.1e15: more than enough for a 10-code budget that
        // gets rotated on disable/re-enroll.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var codes = new List<string>(count);
        Span<byte> buf = stackalloc byte[10];
        for (var i = 0; i < count; i++)
        {
            RandomNumberGenerator.Fill(buf);
            var sb = new StringBuilder(11);
            for (var j = 0; j < buf.Length; j++)
            {
                if (j == 5) sb.Append('-');
                sb.Append(alphabet[buf[j] & 31]);
            }
            codes.Add(sb.ToString());
        }
        return codes;
    }

    public string HashRecoveryCode(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        // Recovery codes carry ~50 bits of entropy each — plenty for
        // SHA-256 without an additional salt/iteration scheme. Normalize
        // to uppercase + strip dashes/whitespace so user typing variants
        // (with or without the formatting dash) all hash identically.
        var canonical = new string(code.Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(char.ToUpperInvariant).ToArray());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}
