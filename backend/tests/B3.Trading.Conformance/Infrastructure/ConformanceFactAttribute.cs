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

            Skip = PlatformEndpoint.SkipReason;
            return;
        }

        if (RequiresAdmin && !peer.HasAdminCredentials)
            Skip = PlatformEndpoint.AdminSkipReason;
    }
}
