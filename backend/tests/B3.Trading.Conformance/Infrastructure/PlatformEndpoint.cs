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
public sealed record PlatformEndpoint(Uri BaseUrl, string Username, string Password)
{
    public const string EnvBaseUrl = "B3T_BASE_URL";
    public const string EnvUsername = "B3T_AUTH_USER";
    public const string EnvPassword = "B3T_AUTH_PASS";

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

        return new PlatformEndpoint(uri, user, pass);
    }

    public const string SkipReason =
        "Conformance platform not configured. Set B3T_BASE_URL, B3T_AUTH_USER, B3T_AUTH_PASS to run.";
}
