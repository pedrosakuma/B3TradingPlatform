using System.Security.Cryptography;
using B3.Trading.Api.Auth.Totp;
using Fido2NetLib;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth.WebAuthn;

public interface IWebAuthnChallengeStore
{
    string PutRegistration(string username, string credentialName, CredentialCreateOptions options);
    string PutAuthentication(
        string username,
        string loginChallengeToken,
        AssertionOptions options);
    bool TryConsumeRegistration(string token, out PendingWebAuthnRegistration? registration);
    bool TryConsumeAuthentication(string token, out PendingWebAuthnAuthentication? authentication);
}

public sealed record PendingWebAuthnRegistration(
    string Username,
    string CredentialName,
    CredentialCreateOptions Options,
    DateTimeOffset CreatedAt);

public sealed record PendingWebAuthnAuthentication(
    string Username,
    string LoginChallengeToken,
    AssertionOptions Options,
    DateTimeOffset CreatedAt);

internal sealed class InMemoryWebAuthnChallengeStore : IWebAuthnChallengeStore
{
    private const int MaxTrackedChallenges = 50_000;
    private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IOptionsMonitor<WebAuthnOptions> _options;
    private readonly TimeProvider _clock;

    public InMemoryWebAuthnChallengeStore(
        IOptionsMonitor<WebAuthnOptions> options,
        TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public string PutRegistration(
        string username,
        string credentialName,
        CredentialCreateOptions options) =>
        Put(new PendingWebAuthnRegistration(
            username, credentialName, options, _clock.GetUtcNow()));

    public string PutAuthentication(
        string username,
        string loginChallengeToken,
        AssertionOptions options) =>
        Put(new PendingWebAuthnAuthentication(
            username, loginChallengeToken, options, _clock.GetUtcNow()));

    public bool TryConsumeRegistration(
        string token,
        out PendingWebAuthnRegistration? registration) =>
        TryConsume(token, out registration);

    public bool TryConsumeAuthentication(
        string token,
        out PendingWebAuthnAuthentication? authentication) =>
        TryConsume(token, out authentication);

    private string Put(object challenge)
    {
        lock (_gate)
        {
            PurgeExpired();
            while (_entries.Count >= MaxTrackedChallenges)
            {
                var oldest = _entries.MinBy(static item => CreatedAt(item.Value));
                _entries.Remove(oldest.Key);
            }

            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            _entries[token] = challenge;
            return token;
        }
    }

    private bool TryConsume<T>(string token, out T? challenge) where T : class
    {
        challenge = null;
        if (string.IsNullOrEmpty(token)) return false;
        lock (_gate)
        {
            if (!_entries.Remove(token, out var found)
                || found is not T typed
                || IsExpired(found))
                return false;
            challenge = typed;
            return true;
        }
    }

    private bool IsExpired(object challenge) =>
        _clock.GetUtcNow() - CreatedAt(challenge) > _options.CurrentValue.ChallengeTtl;

    private void PurgeExpired()
    {
        foreach (var key in _entries
            .Where(item => IsExpired(item.Value))
            .Select(static item => item.Key)
            .ToArray())
        {
            _entries.Remove(key);
        }
    }

    private static DateTimeOffset CreatedAt(object challenge) => challenge switch
    {
        PendingWebAuthnRegistration registration => registration.CreatedAt,
        PendingWebAuthnAuthentication authentication => authentication.CreatedAt,
        _ => DateTimeOffset.MinValue,
    };
}
