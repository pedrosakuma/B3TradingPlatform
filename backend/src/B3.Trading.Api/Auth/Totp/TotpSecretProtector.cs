using Microsoft.AspNetCore.DataProtection;

namespace B3.Trading.Api.Auth.Totp;

/// <summary>
/// Encrypts / decrypts TOTP shared secrets using ASP.NET Core Data
/// Protection so the persisted user file (<c>users.json</c>) never
/// contains plaintext base32 secrets. A leaked file alone is useless
/// without the host's data-protection key ring.
/// </summary>
public interface ITotpSecretProtector
{
    /// <summary>Encrypts a base32 shared secret for at-rest storage.</summary>
    string Protect(string base32Secret);

    /// <summary>Reverses <see cref="Protect"/>; throws on tamper.</summary>
    string Unprotect(string protectedSecret);
}

internal sealed class TotpSecretProtector : ITotpSecretProtector
{
    // Stable purpose string. Changing this rotates secrets out of the
    // existing protection envelope, so it is a one-way migration.
    private const string Purpose = "B3.Trading.Api.Auth.Totp.SharedSecret.v1";

    private readonly IDataProtector _protector;

    public TotpSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string base32Secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(base32Secret);
        return _protector.Protect(base32Secret);
    }

    public string Unprotect(string protectedSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedSecret);
        return _protector.Unprotect(protectedSecret);
    }
}
