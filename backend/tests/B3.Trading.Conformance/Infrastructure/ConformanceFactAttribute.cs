namespace B3.Trading.Conformance.Infrastructure;

/// <summary>
/// xUnit's <see cref="FactAttribute"/> with a discovery-time skip when the
/// participant-side platform isn't configured (see
/// <see cref="PlatformEndpoint.TryResolve"/>). Mirrors the upstream
/// <c>B3.EntryPoint.Conformance.ConformanceFactAttribute</c> so the two
/// suites have the same operator ergonomics: drop env vars, run, ship.
/// </summary>
/// <remarks>
/// When <c>B3T_REQUIRE_CONFIGURED=true</c> is set (the conformance Docker
/// image always sets it), missing/invalid env is treated as a hard failure
/// instead of a silent skip — this prevents a misconfigured CI run from
/// passing with zero tests executed.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConformanceFactAttribute : FactAttribute
{
    public const string EnvRequireConfigured = "B3T_REQUIRE_CONFIGURED";

    /// <summary>
    /// When true, the scenario also requires admin-role credentials
    /// (B3T_ADMIN_USER / B3T_ADMIN_PASS). Skipped at discovery time when
    /// only the basic user creds are configured, so deployments that
    /// don't surface an admin role still get the rest of the suite.
    /// </summary>
    public bool RequiresAdmin { get; init; }

    /// <summary>
    /// When true, the scenario only runs against a host that opted into
    /// synthetic ER injection (<c>POST /api/admin/simulator/er</c> mapped via
    /// <c>Mode=Mock + AllowErInjection=true</c>; operator declares it via
    /// <c>B3T_ER_INJECTION=true</c>, with legacy <c>B3T_SIMULATOR_MODE=true</c>
    /// honored as a fallback). Real / Stub / plain-Mock deployments skip.
    /// </summary>
    public bool RequiresErInjection { get; init; }

    /// <summary>Legacy alias; kept for spec sources that haven't migrated. Same semantics as <see cref="RequiresErInjection"/>.</summary>
    [Obsolete("Use RequiresErInjection — Mode=Simulator was merged into Mode=Mock + AllowErInjection in #163.")]
    public bool RequiresSimulator
    {
        get => RequiresErInjection;
        init => RequiresErInjection = value;
    }

    /// <summary>
    /// When true, the scenario is gated to the dedicated docker-compose
    /// real-stack sandbox (operator opts in via
    /// <c>B3T_REAL_STACK_CONFORMANCE=true</c>). Used for destructive
    /// active-flow tests that submit crossed orders and expect real
    /// trade prints — those would be unsafe to run against any
    /// non-sandbox deployment even when the wires happen to reach.
    /// </summary>
    public bool RequiresSandboxMatching { get; init; }

    /// <summary>
    /// When true, the scenario requires the dedicated market-maker sandbox
    /// stack — real matching + the market-maker-bot overlay resting live
    /// quotes + <c>Trading__Sandbox__AllowSelfCashDeposit=true</c> (operator
    /// opts in via <c>B3T_MARKET_MAKER_SANDBOX=true</c>, #683 item 4). Kept
    /// distinct from <see cref="RequiresSandboxMatching"/> so it runs
    /// against its own isolated CI job/stack instead of one shared with the
    /// self-cross specs the bot's resting orders would otherwise disturb.
    /// </summary>
    public bool RequiresMarketMakerSandbox { get; init; }

    /// <summary>
    /// When true, the scenario requires the dedicated sample-bot sandbox
    /// stack — real matching + market data + the one-shot
    /// <c>B3.Trading.SampleBot</c> overlay already having run (operator
    /// opts in via <c>B3T_SAMPLE_BOT_SANDBOX=true</c>, #722). See
    /// <see cref="PlatformEndpoint.IsSampleBotSandboxEnabled"/>.
    /// </summary>
    public bool RequiresSampleBotSandbox { get; init; }

    /// <summary>
    /// When true, the scenario needs the host's HS256 JWT signing key
    /// (env <c>B3T_AUTH_SIGNING_KEY</c>) so it can mint authentically
    /// signed tokens with custom claims/expiry. Skipped at discovery
    /// time when not configured.
    /// </summary>
    public bool RequiresAuthSigningKey { get; init; }

    /// <summary>
    /// When true, the scenario also requires an operator-opted-in docker
    /// control path (<c>B3T_DOCKER_CONTROL=true</c>) so the test process can
    /// intentionally disconnect and reconnect the matching-platform network
    /// leg. Used by real-stack transport fault-injection specs only.
    /// </summary>
    public bool RequiresDockerControl { get; init; }

    /// <summary>
    /// When true, the scenario requires a running FIXP listener configured
    /// via <c>B3T_FIXP_ENDPOINT</c>. Skipped when the env var is not set.
    /// </summary>
    public bool RequiresFixpListener { get; init; }

    /// <summary>
    /// When true, the scenario requires an mTLS-enabled FIXP listener plus a
    /// trusted client PFX (<c>B3T_FIXP_MTLS_CLIENT_PFX</c>) so the SDK-as-
    /// client mTLS matrix can drive a real handshake. Skipped otherwise.
    /// </summary>
    public bool RequiresFixpMtls { get; init; }

    public ConformanceFactAttribute()
    {
        var peer = PlatformEndpoint.TryResolve();
        if (peer is null)
        {
            var require = Environment.GetEnvironmentVariable(EnvRequireConfigured);
            if (string.Equals(require, "true", StringComparison.OrdinalIgnoreCase) ||
                require == "1")
            {
                // Throwing here makes xUnit surface a discovery error instead of
                // a skip. The CI pipeline fails loudly rather than passing with
                // zero executed tests.
                throw new InvalidOperationException(
                    $"{EnvRequireConfigured}=true but {PlatformEndpoint.SkipReason}");
            }

            // Set base.Skip directly so xUnit honors it without going
            // through the lazy getter (Requires* are still default false
            // here, so the getter would return null).
            base.Skip = PlatformEndpoint.SkipReason;
        }
    }

    /// <summary>
    /// Computed lazily so the <c>RequiresAdmin</c> / <c>RequiresSimulator</c>
    /// init-only properties (set by xUnit AFTER the constructor runs via
    /// object initializer syntax) are visible. Returning a non-null value
    /// here causes xUnit to skip the test at run time. If the constructor
    /// already set a static skip reason (no-peer case), that wins.
    /// </summary>
    public override string? Skip
    {
        get
        {
            if (!string.IsNullOrEmpty(base.Skip)) return base.Skip;
            var peer = PlatformEndpoint.TryResolve();
            if (peer is null) return PlatformEndpoint.SkipReason;
            if (RequiresAdmin && !peer.HasAdminCredentials)
                return PlatformEndpoint.AdminSkipReason;
            if (RequiresErInjection && !PlatformEndpoint.IsErInjectionEnabled())
                return PlatformEndpoint.SimulatorSkipReason;
            if (RequiresSandboxMatching && !PlatformEndpoint.IsRealStackConformance())
                return PlatformEndpoint.RealStackConformanceSkipReason;
            if (RequiresMarketMakerSandbox && !PlatformEndpoint.IsMarketMakerSandboxEnabled())
                return PlatformEndpoint.MarketMakerSandboxSkipReason;
            if (RequiresSampleBotSandbox && !PlatformEndpoint.IsSampleBotSandboxEnabled())
                return PlatformEndpoint.SampleBotSandboxSkipReason;
            if (RequiresDockerControl && !PlatformEndpoint.IsDockerControlEnabled())
                return PlatformEndpoint.DockerControlSkipReason;
            if (RequiresAuthSigningKey && !PlatformEndpoint.IsAuthSigningKeyConfigured())
                return PlatformEndpoint.AuthSigningKeySkipReason;
            if (RequiresFixpListener && !PlatformEndpoint.IsFixpListenerConfigured())
                return PlatformEndpoint.FixpListenerSkipReason;
            if (RequiresFixpMtls && !PlatformEndpoint.IsFixpMtlsConfigured())
                return PlatformEndpoint.FixpMtlsSkipReason;
            return null;
        }
        set => base.Skip = value;
    }
}
