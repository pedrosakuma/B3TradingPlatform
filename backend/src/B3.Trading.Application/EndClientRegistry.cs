using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Stub end-client registry. Real authentication backend (JWT / OIDC / local
/// store) is an open question flagged in issue #1; this exists so the rest of
/// the platform has a stable identity surface to depend on.
/// </summary>
public sealed class EndClientRegistry
{
    private readonly Dictionary<string, EndClientId> _byLogin = new(StringComparer.OrdinalIgnoreCase);

    public EndClientId Register(string login)
    {
        if (string.IsNullOrWhiteSpace(login))
            throw new ArgumentException("login required", nameof(login));

        if (_byLogin.TryGetValue(login, out var existing))
            return existing;

        var id = new EndClientId(login.ToLowerInvariant());
        _byLogin[login] = id;
        return id;
    }

    public bool TryResolve(string login, out EndClientId? id) =>
        _byLogin.TryGetValue(login, out id);
}
