using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Thread-safe end-client registry. Phase 2 binds identities directly from
/// the authenticated JWT <c>sub</c> claim via <see cref="Register"/>; the
/// real user store lives in <c>Trading:Auth</c> config (see Api/Auth).
/// </summary>
public sealed class EndClientRegistry
{
    private readonly ConcurrentDictionary<string, EndClientId> _byLogin =
        new(StringComparer.OrdinalIgnoreCase);

    public EndClientId Register(string login)
    {
        if (string.IsNullOrWhiteSpace(login))
            throw new ArgumentException("login required", nameof(login));

        return _byLogin.GetOrAdd(login, l => new EndClientId(l.ToLowerInvariant()));
    }

    public bool TryResolve(string login, out EndClientId? id)
    {
        var ok = _byLogin.TryGetValue(login, out var found);
        id = found;
        return ok;
    }
}
