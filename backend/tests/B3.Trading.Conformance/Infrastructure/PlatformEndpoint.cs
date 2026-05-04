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
    public const string EnvRealStackConformance = "B3T_REAL_STACK_CONFORMANCE";

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
    /// True when the operator declared the host is running with
    /// <c>Trading:Exchange:Mode=Simulator</c> (env var
    /// <c>B3T_SIMULATOR_MODE=true</c>). Simulator-only scenarios skip
    /// otherwise — the same suite stays valid against Mock/Real/Stub
    /// deployments without false failures.
    /// </summary>
    public static bool IsSimulatorMode()
    {
        var v = Environment.GetEnvironmentVariable(EnvSimulatorMode);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

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

    public const string SkipReason =
        "Conformance platform not configured. Set B3T_BASE_URL, B3T_AUTH_USER, B3T_AUTH_PASS to run.";

    public const string AdminSkipReason =
        "Admin scenario skipped: B3T_ADMIN_USER / B3T_ADMIN_PASS not configured.";

    public const string SimulatorSkipReason =
        "Simulator scenario skipped: B3T_SIMULATOR_MODE=true not set (host is not in Mode=Simulator).";

    public const string RealStackConformanceSkipReason =
        "Real-stack scenario skipped: B3T_REAL_STACK_CONFORMANCE=true not set (host is not the docker-compose real-stack sandbox).";
}
