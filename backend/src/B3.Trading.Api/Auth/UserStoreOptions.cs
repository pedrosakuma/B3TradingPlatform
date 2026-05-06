namespace B3.Trading.Api.Auth;

/// <summary>
/// Slice 3 of #97 hardening: configuration for runtime user persistence.
/// Bound from <c>Trading:Auth:UserStore</c>.
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is <c>false</c>, the host wires the legacy
/// <see cref="InMemoryUserStore"/> and runtime signups evaporate on
/// restart (matches the v0 behavior). Used by integration tests and
/// ephemeral demos that don't want a writable user file. Env-seeded
/// users are NEVER written to disk regardless — they live in
/// configuration and remain authoritative across restarts.
/// </remarks>
public sealed class UserStoreOptions
{
    public const string SectionName = "Trading:Auth:UserStore";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Absolute or relative path to the runtime users JSON file. When
    /// empty (the production default), the host derives
    /// <c>{Trading:Persistence:DataDirectory}/users.json</c> so the
    /// runtime store shares the same lifecycle/durability as the WAL
    /// volume. Operators using a custom data layout can override.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;
}
