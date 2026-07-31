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
    public const string EnvMarketMakerSandbox = "B3T_MARKET_MAKER_SANDBOX";
    public const string EnvSampleBotSandbox = "B3T_SAMPLE_BOT_SANDBOX";
    public const string EnvDockerControl = "B3T_DOCKER_CONTROL";
    public const string EnvAuthSigningKey = "B3T_AUTH_SIGNING_KEY";
    public const string EnvAuthIssuer = "B3T_AUTH_ISSUER";
    public const string EnvAuthAudience = "B3T_AUTH_AUDIENCE";

    // FIXP listener conformance env vars
    public const string EnvFixpEndpoint = "B3T_FIXP_ENDPOINT";
    public const string EnvFixpTls = "B3T_FIXP_TLS";
    public const string EnvFixpCredentialToken = "B3T_FIXP_CREDENTIAL";
    public const string EnvFixpNegotiateBurst = "B3T_FIXP_NEGOTIATE_BURST";

    // mTLS conformance env vars (sub-issue F / RFC §8). The operator points
    // these at a listener configured for client-cert auth and supplies a
    // trusted client PFX; the matrix rows that need wrong-CA/expired/denied
    // material are exercised by the in-process listener suite instead.
    public const string EnvFixpMtlsClientPfx = "B3T_FIXP_MTLS_CLIENT_PFX";
    public const string EnvFixpMtlsClientPfxPass = "B3T_FIXP_MTLS_CLIENT_PFX_PASS";

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
    /// Specs that depend on <c>POST /api/admin/simulator/er</c> being mapped
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
    /// True when the operator declared the target is the dedicated
    /// docker-compose real-stack sandbox WITH the market-maker bot overlay
    /// stacked on top (env var <c>B3T_MARKET_MAKER_SANDBOX=true</c>, #683
    /// item 4). Deliberately a separate flag from
    /// <see cref="IsRealStackConformance"/>: the bot rests its own bid/ask
    /// on each configured instrument, which would otherwise cross with the
    /// same-user Buy+Sell pairs several <c>RequiresSandboxMatching</c>
    /// specs submit to observe a self-print (e.g. <c>MarketDataOutageSpecTests</c>,
    /// <c>ReferencePriceLiveSpecTests</c>) — the venue would match those
    /// against the bot's better-priced resting order instead of the
    /// end-client's own opposite leg, breaking their 1:1 self-fill
    /// assumption. CI runs this scenario against its own isolated stack/job
    /// so the two profiles never share an order book.
    /// </summary>
    public static bool IsMarketMakerSandboxEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnvMarketMakerSandbox);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    /// <summary>
    /// True when the operator declared the target is the dedicated
    /// docker-compose real-stack sandbox WITH the market-maker overlay AND
    /// the sample-bot overlay stacked on top (env var
    /// <c>B3T_SAMPLE_BOT_SANDBOX=true</c>, #722). Gates
    /// <c>Spec_HTTP_SampleBot/SampleBotSmokeSpecTests.cs</c>, which asserts
    /// the one-shot <c>B3.Trading.SampleBot</c> container reached a
    /// terminal order state and left no Working/PartiallyFilled order
    /// behind. Kept distinct from <see cref="IsMarketMakerSandboxEnabled"/>
    /// so CI can run the sample-bot smoke in its own job without implying
    /// every market-maker-sandbox run also expects a sample-bot container
    /// to have executed first.
    /// </summary>
    public static bool IsSampleBotSandboxEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnvSampleBotSandbox);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    /// <summary>
    /// True when the operator declared the test process may actively control
    /// the dockerized real-stack transport (env var
    /// <c>B3T_DOCKER_CONTROL=true</c>). Used by destructive session-roll
    /// specs that intentionally sever the matching-platform TCP leg.
    /// </summary>
    public static bool IsDockerControlEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnvDockerControl);
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
        "ER-injection scenario skipped: neither B3T_ER_INJECTION nor B3T_SIMULATOR_MODE (legacy) is true (host has not opted into POST /api/admin/simulator/er).";

    public const string RealStackConformanceSkipReason =
        "Real-stack scenario skipped: B3T_REAL_STACK_CONFORMANCE=true not set (host is not the docker-compose real-stack sandbox).";

    public const string MarketMakerSandboxSkipReason =
        "Market-maker scenario skipped: B3T_MARKET_MAKER_SANDBOX=true not set (target does not have the market-maker-bot overlay + self-cash-deposit stacked on).";

    public const string SampleBotSandboxSkipReason =
        "Sample-bot scenario skipped: B3T_SAMPLE_BOT_SANDBOX=true not set (target does not have the sample-bot overlay stacked on, or the one-shot sample-bot container has not been run yet).";

    public const string DockerControlSkipReason =
        "Docker-control scenario skipped: B3T_DOCKER_CONTROL=true not set (test process cannot sever/reconnect the matching-platform transport).";

    public const string AuthSigningKeySkipReason =
        "Signing-key scenario skipped: B3T_AUTH_SIGNING_KEY not set (operator must mirror the host's Trading:Auth:SigningKey to mint deterministic tokens).";

    /// <summary>True when the FIXP listener env vars are configured for conformance.</summary>
    public static bool IsFixpListenerConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvFixpEndpoint));

    public const string FixpListenerSkipReason =
        "FIXP listener scenario skipped: B3T_FIXP_ENDPOINT not set.";

    public static int GetFixpNegotiateBurst()
    {
        var value = Environment.GetEnvironmentVariable(EnvFixpNegotiateBurst);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 10;
    }

    /// <summary>
    /// True when an mTLS-enabled FIXP endpoint plus a trusted client PFX are
    /// configured, so the SDK-as-client mTLS conformance rows can run.
    /// </summary>
    public static bool IsFixpMtlsConfigured() =>
        IsFixpListenerConfigured() &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvFixpMtlsClientPfx));

    public const string FixpMtlsSkipReason =
        "FIXP mTLS scenario skipped: set B3T_FIXP_ENDPOINT + B3T_FIXP_MTLS_CLIENT_PFX (trusted client PFX).";
}
