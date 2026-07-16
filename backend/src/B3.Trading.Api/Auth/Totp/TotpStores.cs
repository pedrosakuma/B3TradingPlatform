using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth.Totp;

/// <summary>
/// In-memory bag of TOTP enrollments that have been started but not
/// yet confirmed. An entry lives for <see cref="TotpOptions.PendingEnrollmentTtl"/>
/// (default 5 min) then is treated as expired. Single-host only —
/// matches the rest of the auth stack (<see cref="InMemoryLoginAttemptTracker"/>,
/// <see cref="InMemoryUserStore"/>).
/// </summary>
public interface IPendingTotpEnrollmentStore
{
    /// <summary>Stash a pending enrollment, overwriting any prior one for the user.</summary>
    void Put(string username, PendingTotpEnrollment enrollment);

    /// <summary>Pop the pending enrollment (consume on confirm).</summary>
    bool TryConsume(string username, out PendingTotpEnrollment? enrollment);

    /// <summary>Drop a pending enrollment without consuming (e.g. on disable).</summary>
    void Remove(string username);
}

public sealed record PendingTotpEnrollment(
    string Base32Secret,
    IReadOnlyList<string> RecoveryCodes,
    IReadOnlyList<string> RecoveryCodeHashes,
    DateTimeOffset CreatedAt);

internal sealed class InMemoryPendingTotpEnrollmentStore : IPendingTotpEnrollmentStore
{
    private readonly ConcurrentDictionary<string, PendingTotpEnrollment> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<TotpOptions> _options;
    private readonly TimeProvider _clock;

    public InMemoryPendingTotpEnrollmentStore(IOptionsMonitor<TotpOptions> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public void Put(string username, PendingTotpEnrollment enrollment)
    {
        if (string.IsNullOrEmpty(username)) return;
        PurgeExpired();
        _entries[username] = enrollment;
    }

    public bool TryConsume(string username, out PendingTotpEnrollment? enrollment)
    {
        enrollment = null;
        if (string.IsNullOrEmpty(username)) return false;
        PurgeExpired();
        if (!_entries.TryRemove(username, out var found)) return false;
        if (_clock.GetUtcNow() - found.CreatedAt > _options.CurrentValue.PendingEnrollmentTtl)
            return false;
        enrollment = found;
        return true;
    }

    public void Remove(string username)
    {
        if (string.IsNullOrEmpty(username)) return;
        PurgeExpired();
        _entries.TryRemove(username, out _);
    }

    // Opportunistic sweep: every mutating call drops expired entries.
    // Single-host in-memory store + low-frequency call site (enrollment
    // is rare) makes a background timer overkill; piggy-backing on the
    // normal traffic keeps _entries from growing unbounded for users
    // who Put once and never Consume.
    private void PurgeExpired()
    {
        var ttl = _options.CurrentValue.PendingEnrollmentTtl;
        var now = _clock.GetUtcNow();
        foreach (var kvp in _entries)
        {
            if (now - kvp.Value.CreatedAt > ttl)
                _entries.TryRemove(kvp.Key, out _);
        }
    }
}

/// <summary>
/// Short-lived opaque tokens minted by <c>/auth/login</c> when the
/// caller still has to clear the second factor. The token is bound to
/// the username + a flag indicating whether it grants completion of
/// 2FA verification (existing enrollment) or kicks off forced
/// enrollment (<see cref="UserConfig.Require2FA"/> with no secret yet).
/// </summary>
public interface ITotpChallengeStore
{
    /// <summary>Create + persist a fresh challenge; returns the opaque token.</summary>
    string Issue(string username, TotpChallengeKind kind);

    /// <summary>
    /// Look up a challenge by token. Returns null when unknown or
    /// expired. Does NOT consume — the caller decides whether to
    /// invalidate on success vs. leave for retries.
    /// </summary>
    TotpChallenge? Peek(string token);

    /// <summary>
    /// Atomically consume a challenge of the expected kind. Exactly one
    /// concurrent caller can succeed.
    /// </summary>
    bool TryConsume(string token, TotpChallengeKind expectedKind, out TotpChallenge? challenge);

    /// <summary>Permanently invalidate a challenge (e.g. on successful 2FA).</summary>
    void Invalidate(string token);
}

public enum TotpChallengeKind
{
    /// <summary>User has TOTP enrolled; client must POST a code.</summary>
    Verify = 0,

    /// <summary>User has <see cref="UserConfig.Require2FA"/> set but hasn't enrolled — client must POST /auth/2fa/enroll using this token.</summary>
    ForceEnroll = 1,

    /// <summary>User started mandatory enrollment and must confirm its pending secret before a JWT is issued.</summary>
    VerifyEnrollment = 2,
}

public sealed record TotpChallenge(
    string Username,
    TotpChallengeKind Kind,
    DateTimeOffset IssuedAt);

internal sealed class InMemoryTotpChallengeStore : ITotpChallengeStore
{
    private const int MaxTrackedChallenges = 50_000;

    private readonly Dictionary<string, TotpChallenge> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IOptionsMonitor<TotpOptions> _options;
    private readonly TimeProvider _clock;

    public InMemoryTotpChallengeStore(IOptionsMonitor<TotpOptions> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public string Issue(string username, TotpChallengeKind kind)
    {
        lock (_gate)
        {
            PurgeExpired();
            while (_entries.Count >= MaxTrackedChallenges)
            {
                var oldest = _entries.MinBy(static kvp => kvp.Value.IssuedAt);
                _entries.Remove(oldest.Key);
            }

            // 32 bytes of CSPRNG → base64url (43 chars). Indistinguishable
            // from session-token shape, intentionally opaque to clients.
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            _entries[token] = new TotpChallenge(username, kind, _clock.GetUtcNow());
            return token;
        }
    }

    public TotpChallenge? Peek(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out var ch)) return null;
            if (IsExpired(ch))
            {
                _entries.Remove(token);
                return null;
            }
            return ch;
        }
    }

    public bool TryConsume(string token, TotpChallengeKind expectedKind, out TotpChallenge? challenge)
    {
        challenge = null;
        if (string.IsNullOrEmpty(token)) return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out var found)) return false;
            if (IsExpired(found) || found.Kind != expectedKind)
            {
                if (IsExpired(found)) _entries.Remove(token);
                return false;
            }
            _entries.Remove(token);
            challenge = found;
            return true;
        }
    }

    public void Invalidate(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (_gate)
        {
            _entries.Remove(token);
        }
    }

    private bool IsExpired(TotpChallenge challenge) =>
        _clock.GetUtcNow() - challenge.IssuedAt > _options.CurrentValue.ChallengeTokenTtl;

    private void PurgeExpired()
    {
        foreach (var token in _entries
            .Where(kvp => IsExpired(kvp.Value))
            .Select(static kvp => kvp.Key)
            .ToArray())
        {
            _entries.Remove(token);
        }
    }
}

internal static class WebEncoders
{
    // Tiny base64url shim to avoid pulling in
    // Microsoft.AspNetCore.WebUtilities just for a 6-line helper.
    public static string Base64UrlEncode(byte[] data)
    {
        var s = Convert.ToBase64String(data);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
