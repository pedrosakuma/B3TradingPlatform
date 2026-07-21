using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

/// <summary>
/// In-memory <see cref="IUserStore"/>. Env-seeded users are snapshotted
/// from <see cref="AuthOptions"/> at construction into an immutable
/// dictionary so signup can never accidentally shadow them; runtime
/// users live in a separate <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Both are case-insensitive (matches <see cref="AuthEndpoints"/> login
/// behaviour pre-refactor).
/// </summary>
public sealed class InMemoryUserStore : IUserStore, ILegacyUserSnapshotProvider
{
    private readonly Dictionary<string, UserConfig> _seeded;
    private readonly ConcurrentDictionary<string, UserConfig> _runtime =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _userLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _webAuthnGate = new();

    public InMemoryUserStore(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _seeded = new Dictionary<string, UserConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in options.Value.Users)
        {
            if (string.IsNullOrWhiteSpace(u.Username)) continue;
            // Last-write-wins for duplicate env entries — mirrors how
            // FirstOrDefault used to behave (first wins) only by accident
            // of enumeration order. We pick last-wins so an operator
            // overriding a username in a later config layer actually takes
            // effect; collisions in a single config file are an operator
            // bug regardless.
            _seeded[u.Username] = u;
        }
    }

    public bool TryGet(string username, out UserConfig? user)
    {
        if (string.IsNullOrWhiteSpace(username)) { user = null; return false; }
        if (_seeded.TryGetValue(username, out var s)) { user = s; return true; }
        if (_runtime.TryGetValue(username, out var r)) { user = r; return true; }
        user = null;
        return false;
    }

    public bool TryAdd(UserConfig user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.Username)) return false;
        // Block runtime collisions with env-seeded users: an operator
        // shouldn't have a self-service signup silently shadowed and a
        // self-service signer-up shouldn't be able to "claim" alice.
        if (_seeded.ContainsKey(user.Username)) return false;
        return _runtime.TryAdd(user.Username, user);
    }

    public bool TryUpdate(UserConfig user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.Username)) return false;

        lock (LockFor(user.Username))
        {
            // Env-seeded users: mutate the existing UserConfig in place so
            // TOTP overlays persist for the lifetime of the process. They
            // are intentionally NOT persisted (config is authoritative);
            // operator-controlled accounts re-enroll if the host restarts.
            if (_seeded.TryGetValue(user.Username, out var seeded))
            {
                seeded.Totp = user.Totp;
                seeded.WebAuthnCredentials = user.WebAuthnCredentials;
                seeded.Require2FA = user.Require2FA;
                return true;
            }

            if (!_runtime.ContainsKey(user.Username)) return false;
            _runtime[user.Username] = user;
            return true;
        }
    }

    public bool TryRecordTotpUse(string username, long matchedStep, out UserConfig? updatedUser)
    {
        updatedUser = null;
        if (string.IsNullOrWhiteSpace(username)) return false;

        lock (LockFor(username))
        {
            if (!TryGet(username, out var user) || user is null || user.Totp is null
                || user.Totp.EnrolledAt is null)
                return false;

            if (user.Totp.LastUsedTimeStep is { } prev && matchedStep <= prev)
                return false;

            user.Totp.LastUsedTimeStep = matchedStep;
            updatedUser = user;
            return true;
        }
    }

    public RecoveryCodeConsumeResult TryConsumeRecoveryCode(string username, string codeHash, out UserConfig? updatedUser)
    {
        updatedUser = null;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(codeHash))
            return RecoveryCodeConsumeResult.NotFound;

        lock (LockFor(username))
        {
            if (!TryGet(username, out var user) || user is null || user.Totp is null)
                return RecoveryCodeConsumeResult.NotFound;

            // Constant-time-ish scan: full pass without early-out so
            // timing leaks no information about which index matched.
            var idx = -1;
            for (var i = 0; i < user.Totp.RecoveryCodes.Count; i++)
            {
                if (string.Equals(user.Totp.RecoveryCodes[i], codeHash, StringComparison.Ordinal))
                    idx = i;
            }

            if (idx < 0)
            {
                // Distinguish race-loser / replay from a wrong code so
                // the endpoint can skip the lockout counter increment.
                for (var i = 0; i < user.Totp.ConsumedRecoveryCodes.Count; i++)
                {
                    if (string.Equals(user.Totp.ConsumedRecoveryCodes[i], codeHash, StringComparison.Ordinal))
                        return RecoveryCodeConsumeResult.AlreadyConsumed;
                }
                return RecoveryCodeConsumeResult.NotFound;
            }

            user.Totp.RecoveryCodes.RemoveAt(idx);
            AppendConsumed(user.Totp, codeHash);
            updatedUser = user;
            return RecoveryCodeConsumeResult.Consumed;
        }
    }

    public bool IsWebAuthnCredentialIdUnique(string credentialIdHash)
    {
        if (string.IsNullOrEmpty(credentialIdHash)) return false;
        lock (_webAuthnGate)
        {
            return IsWebAuthnCredentialIdUniqueLocked(credentialIdHash);
        }
    }

    public bool TryAddWebAuthnCredential(
        string username,
        UserWebAuthnCredential credential,
        IReadOnlyList<string>? recoveryCodeHashes,
        out bool recoveryCodesStored,
        out UserConfig? updatedUser)
    {
        recoveryCodesStored = false;
        updatedUser = null;
        if (string.IsNullOrWhiteSpace(username)
            || string.IsNullOrEmpty(credential.CredentialIdHash))
            return false;

        lock (_webAuthnGate)
        {
            lock (LockFor(username))
            {
                if (!TryGet(username, out var user) || user is null
                    || !IsWebAuthnCredentialIdUniqueLocked(credential.CredentialIdHash))
                    return false;
                user.WebAuthnCredentials.Add(credential);
                if (recoveryCodeHashes is { Count: > 0 }
                    && (user.Totp is null || user.Totp.RecoveryCodes.Count == 0))
                {
                    user.Totp ??= new UserTotpConfig();
                    user.Totp.RecoveryCodes = recoveryCodeHashes.ToList();
                    recoveryCodesStored = true;
                }
                updatedUser = user;
                return true;
            }
        }
    }

    private bool IsWebAuthnCredentialIdUniqueLocked(string credentialIdHash) =>
        _seeded.Values.Concat(_runtime.Values)
            .SelectMany(static user => user.WebAuthnCredentials)
            .All(credential => !string.Equals(
                credential.CredentialIdHash, credentialIdHash, StringComparison.Ordinal));

    public bool TryUpdateWebAuthnCounter(
        string username,
        string credentialIdHash,
        uint expectedCounter,
        uint newCounter,
        bool isBackedUp,
        out UserConfig? updatedUser)
    {
        updatedUser = null;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(credentialIdHash))
            return false;

        lock (LockFor(username))
        {
            if (!TryGet(username, out var user) || user is null)
                return false;
            var credential = user.WebAuthnCredentials.FirstOrDefault(item =>
                string.Equals(item.CredentialIdHash, credentialIdHash, StringComparison.Ordinal));
            if (credential is null || credential.SignatureCounter != expectedCounter)
                return false;
            if (newCounter != 0 && newCounter <= expectedCounter)
                return false;
            credential.SignatureCounter = newCounter;
            credential.IsBackedUp = isBackedUp;
            updatedUser = user;
            return true;
        }
    }

    private static void AppendConsumed(UserTotpConfig totp, string codeHash)
    {
        // FIFO eviction: drop oldest entries until we're under the
        // cap. The cap is sized for many enrollments' worth of churn,
        // so eviction is rare; the loop tolerates a cap reduced via
        // future config without unbounded growth.
        while (totp.ConsumedRecoveryCodes.Count >= UserTotpConfig.ConsumedRecoveryCodesCap)
            totp.ConsumedRecoveryCodes.RemoveAt(0);
        totp.ConsumedRecoveryCodes.Add(codeHash);
    }

    private object LockFor(string username)
        => _userLocks.GetOrAdd(username, _ => new object());

    public IReadOnlyList<UserConfig> SnapshotUsers()
    {
        lock (_seeded)
        {
            return _seeded.Values
                .Concat(_runtime.Values)
                .Where(u => !string.IsNullOrWhiteSpace(u.Username))
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
