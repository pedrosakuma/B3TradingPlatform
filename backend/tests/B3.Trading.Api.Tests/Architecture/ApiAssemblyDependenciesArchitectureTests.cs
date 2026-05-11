using System.Reflection;

namespace B3.Trading.Api.Tests.Architecture;

/// <summary>
/// Architectural guardrails for the Api assembly (#188).
///
/// The Api project is the presentation layer; it must depend only on
/// <c>B3.Trading.Application</c> (and <c>B3.Trading.Domain</c>) plus the
/// ASP.NET Core framework. Re-introducing a reference to
/// <c>B3.Trading.Infrastructure</c> or <c>B3.Trading.EntryPointListener</c>
/// would let endpoint code reach into concrete adapters again — exactly the
/// regression this test is here to prevent.
///
/// <para>If a future endpoint genuinely needs a type currently in
/// Infrastructure or the listener, the right answer is to introduce an
/// Application-layer port (interface) and have the concrete project
/// implement it — not to re-add the project reference.</para>
/// </summary>
public sealed class ApiAssemblyDependenciesArchitectureTests
{
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "B3.Trading.Infrastructure",
        "B3.Trading.EntryPointListener",
    ];

    [Fact]
    public void ApiAssembly_DoesNotReference_InfrastructureOrEntryPointListener()
    {
        var apiAssembly = typeof(B3.Trading.Api.AdminEndpoints).Assembly;
        var referenced = apiAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        var forbidden = ForbiddenAssemblyNames
            .Where(name => referenced.Contains(name))
            .ToArray();

        Assert.True(
            forbidden.Length == 0,
            $"B3.Trading.Api must not reference {string.Join(", ", forbidden)}. " +
            "Introduce an Application-layer interface and have the concrete " +
            "project implement it instead of re-adding the project reference.");
    }
}
