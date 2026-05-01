namespace B3.Trading.Conformance.Infrastructure;

/// <summary>
/// xUnit's <see cref="FactAttribute"/> with a discovery-time skip when the
/// participant-side platform isn't configured (see
/// <see cref="PlatformEndpoint.TryResolve"/>). Mirrors the upstream
/// <c>B3.EntryPoint.Conformance.ConformanceFactAttribute</c> so the two
/// suites have the same operator ergonomics: drop env vars, run, ship.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConformanceFactAttribute : FactAttribute
{
    public ConformanceFactAttribute()
    {
        if (PlatformEndpoint.TryResolve() is null)
            Skip = PlatformEndpoint.SkipReason;
    }
}
