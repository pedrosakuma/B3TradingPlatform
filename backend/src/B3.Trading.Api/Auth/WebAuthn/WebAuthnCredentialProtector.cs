using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace B3.Trading.Api.Auth.WebAuthn;

public interface IWebAuthnCredentialProtector
{
    string Protect(byte[] value);
    byte[] Unprotect(string protectedValue);
    string HashCredentialId(byte[] credentialId);
}

internal sealed class WebAuthnCredentialProtector : IWebAuthnCredentialProtector
{
    private const string Purpose = "B3.Trading.Api.Auth.WebAuthn.Credential.v1";
    private readonly IDataProtector _protector;

    public WebAuthnCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _protector.Protect(Convert.ToBase64String(value));
    }

    public byte[] Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedValue);
        return Convert.FromBase64String(_protector.Unprotect(protectedValue));
    }

    public string HashCredentialId(byte[] credentialId)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        return Convert.ToHexString(SHA256.HashData(credentialId));
    }
}
