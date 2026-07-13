using System.Security.Cryptography;
using B3.Trading.Application.Persistence;
using BCrypt.Net;

namespace B3.Trading.Application.UserBots;

/// <summary>
/// In-memory implementation of <see cref="IUserBotCredentialRegistry"/>
/// that is reconstructed on cold start by replaying
/// <see cref="UserBotCredentialCreatedEvent"/> +
/// <see cref="UserBotCredentialRevokedEvent"/> on top of the latest
/// snapshot. Mutations go through <see cref="EventDispatcher"/> so the
/// (WAL append, in-memory mutation) pair is atomic with respect to
/// snapshot capture (RFC §4.9 invariant).
/// </summary>
public sealed class InMemoryUserBotCredentialRegistry : IUserBotCredentialRegistry
{
    /// <summary>
    /// Length of the public, embeddable short-id portion of the PAT.
    /// Fixed-length parsing is required because the secret half is
    /// base64url-encoded and may itself contain <c>_</c>, so a simple
    /// <c>IndexOf('_')</c> split would break.
    /// </summary>
    internal const int ShortIdChars = 10;

    /// <summary>RFC §4.5 — bcrypt cost factor. ≈250ms/op on commodity HW.</summary>
    internal const int BcryptCost = 12;

    /// <summary>Hex length of a SHA-256 client-cert thumbprint pin (RFC §4.3).</summary>
    internal const int Sha256HexChars = 64;

    private const string TokenPrefix = "b3t_";

    private readonly EventDispatcher? _dispatcher;
    private readonly object _gate = new();
    // Indexed both by primary id (for revoke-by-id) and by short-id (for
    // FIXP authenticate, which only sees the short-id half of the PAT).
    private readonly Dictionary<Guid, UserBotCredential> _byId = new();
    private readonly Dictionary<string, UserBotCredential> _byShortId =
        new(StringComparer.Ordinal);

    public InMemoryUserBotCredentialRegistry() : this(null) { }

    public InMemoryUserBotCredentialRegistry(EventDispatcher? dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<CreatedUserBotCredential> CreateAsync(
        string userId, string label, CancellationToken ct) =>
        CreateAsync(userId, label, boundCertThumbprint: null, ct);

    public Task<CreatedUserBotCredential> CreateAsync(
        string userId, string label, string? boundCertThumbprint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var pin = NormalizeBoundThumbprint(boundCertThumbprint);

        var shortId = MintShortId();
        var secret = MintSecret();
        var hash = BCrypt.Net.BCrypt.HashPassword(secret, workFactor: BcryptCost);

        var credential = new UserBotCredential(
            Id: Guid.NewGuid(),
            UserId: userId,
            CredShortId: shortId,
            Label: label.Trim(),
            SecretHash: hash,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RevokedAtUtc: null,
            BoundCertThumbprint: pin);

        var evt = new UserBotCredentialCreatedEvent
        {
            Id = credential.Id,
            UserId = credential.UserId,
            CredShortId = credential.CredShortId,
            Label = credential.Label,
            SecretHash = credential.SecretHash,
            CreatedAtUtc = credential.CreatedAtUtc,
            BoundCertThumbprint = credential.BoundCertThumbprint,
        };

        if (_dispatcher is not null)
            _dispatcher.Dispatch(evt, () => ApplyCreated(credential));
        else
            ApplyCreated(credential);

        var plainToken = $"{TokenPrefix}{shortId}_{secret}";
        return Task.FromResult(new CreatedUserBotCredential(credential, plainToken));
    }

    public Task<bool> SetBoundCertThumbprintAsync(
        string userId, Guid credentialId, string? boundCertThumbprint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Validate/normalize before the existence check so a malformed pin is
        // a 400 regardless of whether the id exists (no id-oracle leak via the
        // error-shape difference).
        var pin = NormalizeBoundThumbprint(boundCertThumbprint);

        lock (_gate)
        {
            if (!_byId.TryGetValue(credentialId, out var existing))
                return Task.FromResult(false);
            // Cross-user lookups must be indistinguishable from missing.
            if (!string.Equals(existing.UserId, userId, StringComparison.Ordinal))
                return Task.FromResult(false);
            if (existing.RevokedAtUtc is not null)
                return Task.FromResult(false);
        }

        var changedAt = DateTimeOffset.UtcNow;
        var evt = new UserBotCredentialCertBindingChangedEvent
        {
            Id = credentialId,
            UserId = userId,
            ChangedAtUtc = changedAt,
            BoundCertThumbprint = pin,
        };

        if (_dispatcher is not null)
            _dispatcher.Dispatch(evt, () => ApplyCertBindingChanged(credentialId, pin));
        else
            ApplyCertBindingChanged(credentialId, pin);

        return Task.FromResult(true);
    }

    public Task<bool> RevokeAsync(string userId, Guid credentialId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (_gate)
        {
            if (!_byId.TryGetValue(credentialId, out var existing))
                return Task.FromResult(false);
            // Cross-user lookups must be indistinguishable from missing.
            if (!string.Equals(existing.UserId, userId, StringComparison.Ordinal))
                return Task.FromResult(false);
            if (existing.RevokedAtUtc is not null)
                return Task.FromResult(false);
        }

        var revokedAt = DateTimeOffset.UtcNow;
        var evt = new UserBotCredentialRevokedEvent
        {
            Id = credentialId,
            UserId = userId,
            RevokedAtUtc = revokedAt,
        };

        if (_dispatcher is not null)
            _dispatcher.Dispatch(evt, () => ApplyRevoked(credentialId, revokedAt));
        else
            ApplyRevoked(credentialId, revokedAt);

        return Task.FromResult(true);
    }

    public IReadOnlyList<UserBotCredential> ListByUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
        {
            return _byId.Values
                .Where(c => string.Equals(c.UserId, userId, StringComparison.Ordinal))
                .OrderBy(c => c.CreatedAtUtc)
                .ToList();
        }
    }

    public Task<UserBotCredential?> TryAuthenticateAsync(
        string presentedToken, CancellationToken ct)
    {
        if (!TryParseToken(presentedToken, out var shortId, out var secret))
            return Task.FromResult<UserBotCredential?>(null);

        UserBotCredential? candidate;
        lock (_gate)
        {
            if (!_byShortId.TryGetValue(shortId, out candidate))
                return Task.FromResult<UserBotCredential?>(null);
            if (candidate.RevokedAtUtc is not null)
                return Task.FromResult<UserBotCredential?>(null);
        }

        // bcrypt verify is intentionally outside the lock — it's the
        // hot path for the listener and would otherwise serialise every
        // FIXP Negotiate behind the registry mutex.
        bool ok;
        try
        {
            ok = BCrypt.Net.BCrypt.Verify(secret, candidate.SecretHash);
        }
        catch (SaltParseException)
        {
            // Defensive: a malformed hash on disk should not crash the
            // listener — treat as auth failure and let the credential
            // surface naturally as "won't authenticate" from the UI.
            ok = false;
        }
        return Task.FromResult(ok ? candidate : null);
    }

    /// <summary>
    /// Snapshot capture hook (called by <c>StateSnapshotter</c> under
    /// the dispatcher lock). Returns a deterministic, ordered copy of
    /// the persistent fields.
    /// </summary>
    public IReadOnlyList<UserBotCredentialSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _byId.Values
                .OrderBy(c => c.CreatedAtUtc)
                .ThenBy(c => c.Id)
                .Select(c => new UserBotCredentialSnapshot(
                    c.Id, c.UserId, c.CredShortId, c.Label, c.SecretHash,
                    c.CreatedAtUtc, c.RevokedAtUtc, c.BoundCertThumbprint))
                .ToList();
        }
    }

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8 / P6). Copies the underlying immutable
    /// <see cref="UserBotCredential"/> records into a fresh array under
    /// <c>_gate</c> and returns it; deterministic ordering and
    /// <see cref="UserBotCredentialSnapshot"/> DTO allocation move to the
    /// projection step. The records themselves are immutable so reading
    /// them outside the registry lock is safe by construction.
    /// </summary>
    public UserBotCredential[] RawSnapshot()
    {
        lock (_gate)
        {
            if (_byId.Count == 0) return Array.Empty<UserBotCredential>();
            var raw = new UserBotCredential[_byId.Count];
            _byId.Values.CopyTo(raw, 0);
            return raw;
        }
    }

    /// <summary>Snapshot restore hook (single-threaded at startup).</summary>
    public void Restore(IEnumerable<UserBotCredentialSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        lock (_gate)
        {
            _byId.Clear();
            _byShortId.Clear();
            foreach (var s in snapshots)
            {
                var c = new UserBotCredential(
                    s.Id, s.UserId, s.CredShortId, s.Label, s.SecretHash,
                    s.CreatedAtUtc, s.RevokedAtUtc, s.BoundCertThumbprint);
                _byId[c.Id] = c;
                _byShortId[c.CredShortId] = c;
            }
        }
    }

    /// <summary>Replay hook for <see cref="UserBotCredentialCreatedEvent"/>.</summary>
    internal void ApplyCreated(UserBotCredential credential)
    {
        lock (_gate)
        {
            _byId[credential.Id] = credential;
            _byShortId[credential.CredShortId] = credential;
        }
    }

    /// <summary>Replay hook for <see cref="UserBotCredentialRevokedEvent"/>.</summary>
    internal void ApplyRevoked(Guid credentialId, DateTimeOffset revokedAtUtc)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(credentialId, out var existing)) return;
            var updated = existing with { RevokedAtUtc = revokedAtUtc };
            _byId[credentialId] = updated;
            _byShortId[updated.CredShortId] = updated;
        }
    }

    /// <summary>Replay hook for <see cref="UserBotCredentialCertBindingChangedEvent"/>.</summary>
    internal void ApplyCertBindingChanged(Guid credentialId, string? boundCertThumbprint)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(credentialId, out var existing)) return;
            var updated = existing with { BoundCertThumbprint = boundCertThumbprint };
            _byId[credentialId] = updated;
            _byShortId[updated.CredShortId] = updated;
        }
    }

    /// <summary>
    /// Canonicalizes a caller-supplied pin to upper-case 64-hex, or returns
    /// <c>null</c> for an empty/whitespace value (clear). Throws
    /// <see cref="ArgumentException"/> for any non-empty value that is not a
    /// valid SHA-256 thumbprint — colons and internal whitespace are stripped
    /// first so PEM/openssl-style <c>AA:BB:…</c> input is accepted.
    /// </summary>
    internal static string? NormalizeBoundThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return null;

        // Fixed-size buffer (never sized from untrusted input) — anything that
        // produces more than Sha256HexChars significant characters is malformed.
        Span<char> buffer = stackalloc char[Sha256HexChars];
        var n = 0;
        foreach (var ch in thumbprint)
        {
            if (ch is ':' or ' ' or '\t' or '\r' or '\n')
                continue;
            if (n >= Sha256HexChars)
            {
                throw new ArgumentException(
                    "BoundCertThumbprint must be a 64-character hex SHA-256 thumbprint.",
                    nameof(thumbprint));
            }
            buffer[n++] = char.ToUpperInvariant(ch);
        }

        if (n == 0)
            return null;

        var normalized = buffer[..n];
        if (n != Sha256HexChars || !IsHex(normalized))
        {
            throw new ArgumentException(
                "BoundCertThumbprint must be a 64-character hex SHA-256 thumbprint.",
                nameof(thumbprint));
        }

        return new string(normalized);
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (var ch in s)
        {
            if (ch is (>= '0' and <= '9') or (>= 'A' and <= 'F'))
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parses <c>b3t_&lt;shortId&gt;_&lt;secret&gt;</c>. Uses the fixed
    /// <see cref="ShortIdChars"/> length rather than splitting on the
    /// first <c>_</c> because base64url-encoded secrets legitimately
    /// contain <c>_</c> characters.
    /// </summary>
    internal static bool TryParseToken(string token, out string shortId, out string secret)
    {
        shortId = string.Empty;
        secret = string.Empty;
        if (string.IsNullOrEmpty(token)) return false;
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return false;

        var rest = token.AsSpan(TokenPrefix.Length);
        // shortId + '_' + at least one secret char
        if (rest.Length < ShortIdChars + 2) return false;
        if (rest[ShortIdChars] != '_') return false;

        shortId = rest[..ShortIdChars].ToString();
        secret = rest[(ShortIdChars + 1)..].ToString();
        return true;
    }

    private static string MintShortId()
    {
        // 8 random bytes → 11 base64url chars; trim to fixed ShortIdChars
        // length. ≈60 bits of entropy is plenty for an O(N) lookup table
        // that holds at most a few credentials per user.
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes)[..ShortIdChars];
    }

    private static string MintSecret()
    {
        // 24 random bytes → 32 base64url chars (≈192 bits). bcrypt-cost-12
        // verify on the listener side gates brute force.
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
