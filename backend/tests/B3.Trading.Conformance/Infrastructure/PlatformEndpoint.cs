namespace B3.Trading.Conformance.Infrastructure;

/// <summary>
/// Resolves the participant-side platform endpoint + a smoke-test user
/// from environment variables. The same conformance suite targets either a
/// locally-running <c>B3.Trading.Host</c> (developer loop) or a deployed
/// platform (UAT / staging) — only env values change.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the upstream <c>B3.EntryPoint.Conformance/PeerEndpoint</c>
/// pattern, but for the API/WS contract instead of the FIXP wire. When
/// the env vars are not set, every conformance scenario is skipped at
/// discovery time so CI stays green without a deployed instance.
/// </para>
/// <para>
/// Why not in-process <c>WebApplicationFactory</c>? Because conformance is
/// the contract between the platform and its consumers — the contract has
/// to hold against a real running process behind real HTTP, not just
/// against a TestServer. Component-level tests live in
/// <c>tests/B3.Trading.Api.Tests</c>.
/// </para>
/// </remarks>
public sealed record PlatformEndpoint(
    Uri BaseUrl,
    string Username,
    string Password,
    string? AdminUsername = null,
    string? AdminPassword = null)
{
    public const string EnvBaseUrl = "B3T_BASE_URL";
    public const string EnvUsername = "B3T_AUTH_USER";
    public const string EnvPassword = "B3T_AUTH_PASS";
    public const string EnvAdminUsername = "B3T_ADMIN_USER";
    public const string EnvAdminPassword = "B3T_ADMIN_PASS";
    public const string EnvSimulatorMode = "B3T_SIMULATOR_MODE";
    public const string EnvErInjection = "B3T_ER_INJECTION";
    public const string EnvRealStackConformance = "B3T_REAL_STACK_CONFORMANCE";
    public const string EnvAuthSigningKey = "B3T_AUTH_SIGNING_KEY";
    public const string EnvAuthIssuer = "B3T_AUTH_ISSUER";
    public const string EnvAuthAudience = "B3T_AUTH_AUDIENCE";

    public static PlatformEndpoint? TryResolve()
    {
        var baseUrl = Environment.GetEnvironmentVariable(EnvBaseUrl);
        var user = Environment.GetEnvironmentVariable(EnvUsername);
        var pass = Environment.GetEnvironmentVariable(EnvPassword);

        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(pass))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return null;

        // Admin creds are optional — scenarios that need them skip
        // individually via HasAdminCredentials.
        var adminUser = Environment.GetEnvironmentVariable(EnvAdminUsername);
        var adminPass = Environment.GetEnvironmentVariable(EnvAdminPassword);
        var hasAdmin = !string.IsNullOrWhiteSpace(adminUser) && !string.IsNullOrWhiteSpace(adminPass);

        return new PlatformEndpoint(
            uri, user, pass,
            hasAdmin ? adminUser : null,
            hasAdmin ? adminPass : null);
    }

    public bool HasAdminCredentials =>
        !string.IsNullOrWhiteSpace(AdminUsername) && !string.IsNullOrWhiteSpace(AdminPassword);

    /// <summary>
    /// True when the operator declared the host accepts synthetic ER
    /// injection (env <c>B3T_ER_INJECTION=true</c>). The legacy env var
    /// <c>B3T_SIMULATOR_MODE</c> is honored as a fallback for
    /// runbooks/.env files that predate #163's
    /// <c>Mode=Simulator</c> → <c>Mode=Mock + AllowErInjection</c> migration.
    /// Specs that depend on <c>POST /admin/simulator/er</c> being mapped
    /// skip when neither env var is set — the same suite stays valid
    /// against Real / Stub / plain-Mock deployments without false failures.
    /// </summary>
    public static bool IsErInjectionEnabled()
    {
        var primary = Environment.GetEnvironmentVariable(EnvErInjection);
        if (string.Equals(primary, "true", StringComparison.OrdinalIgnoreCase) || primary == "1")
            return true;
        var legacy = Environment.GetEnvironmentVariable(EnvSimulatorMode);
        return string.Equals(legacy, "true", StringComparison.OrdinalIgnoreCase) || legacy == "1";
    }

    /// <summary>Legacy name; kept for any external caller that hasn't migrated. Same semantics as <see cref="IsErInjectionEnabled"/>.</summary>
    [Obsolete("Use IsErInjectionEnabled — Mode=Simulator was merged into Mode=Mock + AllowErInjection in #163.")]
    public static bool IsSimulatorMode() => IsErInjectionEnabled();

    /// <summary>
    /// True when the operator declared this is the dedicated docker-compose
    /// real-stack sandbox (env var <c>B3T_REAL_STACK_CONFORMANCE=true</c>).
    /// Gates destructive scenarios that submit live crossed orders and
    /// expect them to print real trades — running those against
    /// staging/prod-like infrastructure would be unsafe even if the
    /// matching+marketdata wires happened to be reachable.
    /// </summary>
    public static bool IsRealStackConformance()
    {
        var v = Environment.GetEnvironmentVariable(EnvRealStackConformance);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    /// <summary>
    /// True when the operator wired the host's HS256 JWT signing key into
    /// the conformance environment via <c>B3T_AUTH_SIGNING_KEY</c>. Tests
    /// that need to mint deterministic tokens (e.g. expired-JWT rejection
    /// scenarios) require the same key the host validates with — that's
    /// the only way to assert that an authentically-signed-but-expired
    /// token is rejected vs. just an unsigned/garbage one.
    /// </summary>
    public static bool IsAuthSigningKeyConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvAuthSigningKey));

    public static string GetAuthSigningKey() =>
        Environment.GetEnvironmentVariable(EnvAuthSigningKey)
            ?? throw new InvalidOperationException(
                $"{EnvAuthSigningKey} not set. Gate the test with RequiresAuthSigningKey=true.");

    /// <summary>
    /// Issuer claim the host validates against. Defaults to the upstream
    /// <see cref="JwtIssuer"/> default; override via env if the operator
    /// changed <c>Trading:Auth:Issuer</c>.
    /// </summary>
    public static string GetAuthIssuer() =>
        Environment.GetEnvironmentVariable(EnvAuthIssuer) ?? "b3-trading";

    public static string GetAuthAudience() =>
        Environment.GetEnvironmentVariable(EnvAuthAudience) ?? "b3-trading-clients";

    public const string SkipReason =
        "Conformance platform not configured. Set B3T_BASE_URL, B3T_AUTH_USER, B3T_AUTH_PASS to run.";

    public const string AdminSkipReason =
        "Admin scenario skipped: B3T_ADMIN_USER / B3T_ADMIN_PASS not configured.";

    public const string SimulatorSkipReason =
        "ER-injection scenario skipped: neither B3T_ER_INJECTION nor B3T_SIMULATOR_MODE (legacy) is true (host has not opted into POST /admin/simulator/er).";

    public const string RealStackConformanceSkipReason =
        "Real-stack scenario skipped: B3T_REAL_STACK_CONFORMANCE=true not set (host is not the docker-compose real-stack sandbox).";

    public const string AuthSigningKeySkipReason =
        "Signing-key scenario skipped: B3T_AUTH_SIGNING_KEY not set (operator must mirror the host's Trading:Auth:SigningKey to mint deterministic tokens).";
}
