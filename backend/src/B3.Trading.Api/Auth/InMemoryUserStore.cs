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
public sealed class InMemoryUserStore : IUserStore
{
    private readonly Dictionary<string, UserConfig> _seeded;
    private readonly ConcurrentDictionary<string, UserConfig> _runtime =
        new(StringComparer.OrdinalIgnoreCase);

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

        // Env-seeded users: mutate the existing UserConfig in place so
        // TOTP overlays persist for the lifetime of the process. They
        // are intentionally NOT persisted (config is authoritative);
        // operator-controlled accounts re-enroll if the host restarts.
        if (_seeded.TryGetValue(user.Username, out var seeded))
        {
            seeded.Totp = user.Totp;
            seeded.Require2FA = user.Require2FA;
            return true;
        }

        if (!_runtime.ContainsKey(user.Username)) return false;
        _runtime[user.Username] = user;
        return true;
    }
}
