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
    /// When true, the scenario only runs against a host configured with
    /// <c>Trading:Exchange:Mode=Simulator</c> (operator declares it via
    /// <c>B3T_SIMULATOR_MODE=true</c>). Mock/Real/Stub deployments skip.
    /// </summary>
    public bool RequiresSimulator { get; init; }

    /// <summary>
    /// When true, the scenario is gated to the dedicated docker-compose
    /// real-stack sandbox (operator opts in via
    /// <c>B3T_REAL_STACK_CONFORMANCE=true</c>). Used for destructive
    /// active-flow tests that submit crossed orders and expect real
    /// trade prints — those would be unsafe to run against any
    /// non-sandbox deployment even when the wires happen to reach.
    /// </summary>
    public bool RequiresSandboxMatching { get; init; }

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
            if (RequiresSimulator && !PlatformEndpoint.IsSimulatorMode())
                return PlatformEndpoint.SimulatorSkipReason;
            if (RequiresSandboxMatching && !PlatformEndpoint.IsRealStackConformance())
                return PlatformEndpoint.RealStackConformanceSkipReason;
            return null;
        }
        set => base.Skip = value;
    }
}
