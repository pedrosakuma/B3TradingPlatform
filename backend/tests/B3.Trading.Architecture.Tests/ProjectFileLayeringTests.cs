using System.Xml.Linq;

namespace B3.Trading.Architecture.Tests;

/// <summary>
/// Declaration-time companion to <see cref="LayeringArchitectureTests"/>.
///
/// <para>The metadata-based suite asserts <em>actual</em> coupling
/// (<see cref="System.Reflection.Assembly.GetReferencedAssemblies"/> only
/// surfaces references the compiler kept because a type was used). That
/// leaves one drift window: a forbidden <c>ProjectReference</c> /
/// <c>PackageReference</c> can be <em>declared</em> in a <c>.csproj</c>
/// and stay invisible until the first type from it is used. For a
/// governance harness whose whole point is to stop disordered growth
/// early, we also enforce the seam at declaration level by parsing the
/// production project files directly.</para>
/// </summary>
public sealed class ProjectFileLayeringTests
{
    private const string Domain = "B3.Trading.Domain";
    private const string Application = "B3.Trading.Application";
    private const string Infrastructure = "B3.Trading.Infrastructure";
    private const string Api = "B3.Trading.Api";
    private const string EntryPointListener = "B3.Trading.EntryPointListener";

    private const string SdkGatewayClient = "B3.EntryPoint.Client";
    private const string SdkSbeCodec = "B3.EntryPoint.Sbe";

    [Fact]
    public void Domain_ProjectFile_DeclaresNoInternalReferenceAndNoSdk()
    {
        AssertProjectReferencesSubsetOf(Domain, allowed: []);
        AssertNoPackageReference(Domain, SdkGatewayClient, SdkSbeCodec);
    }

    [Fact]
    public void Application_ProjectFile_OnlyReferencesDomainAndNoSdk()
    {
        AssertProjectReferencesSubsetOf(Application, allowed: [Domain]);
        AssertNoPackageReference(Application, SdkGatewayClient, SdkSbeCodec);
    }

    [Fact]
    public void Api_ProjectFile_OnlyReferencesApplicationAndDomainAndNoSdk()
    {
        AssertProjectReferencesSubsetOf(Api, allowed: [Domain, Application]);
        AssertNoPackageReference(Api, SdkGatewayClient, SdkSbeCodec);
    }

    [Fact]
    public void EntryPointListener_ProjectFile_DoesNotDeclareTheGatewayClient()
    {
        AssertProjectReferencesSubsetOf(EntryPointListener, allowed: [Domain, Application]);
        // The SBE codec is allowed (the listener decodes inbound frames);
        // the order-entry gateway client is not.
        AssertNoPackageReference(EntryPointListener, SdkGatewayClient);
    }

    [Fact]
    public void Infrastructure_ProjectFile_DeclaresTheGatewayClient()
    {
        var packages = PackageReferencesOf(Infrastructure);
        Assert.True(
            packages.Contains(SdkGatewayClient),
            $"Expected '{Infrastructure}.csproj' to declare a PackageReference to " +
            $"'{SdkGatewayClient}' — it is the IExchangeGateway adapter. If this moved, " +
            "update the guard; the wire SDK must live behind exactly one internal project.");
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static void AssertProjectReferencesSubsetOf(string project, string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var offenders = ProjectReferencesOf(project)
            .Where(r => !allowedSet.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        var allowedDisplay = allowed.Length == 0 ? "(none)" : string.Join(", ", allowed);
        Assert.True(
            offenders.Length == 0,
            $"'{project}.csproj' may only declare ProjectReferences to: {allowedDisplay}. " +
            $"Forbidden: {string.Join(", ", offenders)}. Introduce an Application-layer port " +
            "instead of an upward/sideways project reference (AGENTS.md layering seam).");
    }

    private static void AssertNoPackageReference(string project, params string[] forbidden)
    {
        var packages = PackageReferencesOf(project);
        var hits = forbidden.Where(packages.Contains).ToArray();
        Assert.True(
            hits.Length == 0,
            $"'{project}.csproj' must not declare a PackageReference to: " +
            $"{string.Join(", ", hits)}. The wire SDK lives behind IExchangeGateway in " +
            "Infrastructure; declaring it here couples a higher layer to wire types.");
    }

    private static HashSet<string> ProjectReferencesOf(string project)
        => ReadIncludes(project, "ProjectReference")
            .Select(LastPathSegmentWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> PackageReferencesOf(string project)
        => ReadIncludes(project, "PackageReference").ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> ReadIncludes(string project, string elementName)
    {
        var csprojPath = ProjectFilePath(project);
        var doc = XDocument.Load(csprojPath);
        // The SDK-style project files in this repo are MSBuild-namespace-less,
        // so a local-name match is both sufficient and robust to namespaces.
        return doc.Descendants()
            .Where(e => e.Name.LocalName == elementName)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());
    }

    private static string LastPathSegmentWithoutExtension(string include)
    {
        // ProjectReference Include uses Windows-style separators on disk
        // (e.g. "..\B3.Trading.Domain\B3.Trading.Domain.csproj").
        var segment = include.Split('\\', '/').Last();
        return segment.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? segment[..^".csproj".Length]
            : segment;
    }

    private static string ProjectFilePath(string project)
    {
        var path = Path.Combine(RepoRoot(), "backend", "src", project, project + ".csproj");
        Assert.True(File.Exists(path), $"Expected production project file at '{path}'.");
        return path;
    }

    private static string RepoRoot()
    {
        // Walk up from the test output directory until the solution file is
        // found. Stable regardless of the bin/<config>/<tfm> nesting depth.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "B3TradingPlatform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (B3TradingPlatform.slnx) by walking up " +
            $"from '{AppContext.BaseDirectory}'.");
    }
}
