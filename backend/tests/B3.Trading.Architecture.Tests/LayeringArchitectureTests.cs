using System.Reflection;

namespace B3.Trading.Architecture.Tests;

/// <summary>
/// Executable guardrails for the layered architecture documented in
/// <c>AGENTS.md</c> ("Layered architecture — respect the seam"):
///
/// <code>
///   Domain → Application → Infrastructure → Host → Api / EntryPointListener
/// </code>
///
/// These rules used to be enforced only by reviewer vigilance plus the
/// happenstance that the offending <c>.csproj</c> never declared the
/// reference. They are the single most load-bearing convention in the
/// repo — the wire SDK (<c>B3.EntryPoint.Client</c>) lives behind
/// <c>IExchangeGateway</c> in <c>Infrastructure</c>, and every SDK bump
/// (0.15.0 / 0.16.0) survived precisely because <c>Domain</c> and
/// <c>Application</c> never coupled to it. This suite turns that
/// convention into a failing build so disordered growth cannot erode it.
///
/// <para>The checks inspect each assembly's <em>direct</em> metadata
/// references (<see cref="Assembly.GetReferencedAssemblies"/>), which is
/// exactly "does this assembly actually couple to that one" — transitive
/// references are deliberately not flattened, since layering is about
/// direct coupling.</para>
/// </summary>
public sealed class LayeringArchitectureTests
{
    private const string Domain = "B3.Trading.Domain";
    private const string Application = "B3.Trading.Application";
    private const string Infrastructure = "B3.Trading.Infrastructure";
    private const string Api = "B3.Trading.Api";
    private const string Host = "B3.Trading.Host";
    private const string EntryPointListener = "B3.Trading.EntryPointListener";

    // The wire SDK is two assemblies: the order-entry gateway client and
    // the SBE codec. The gateway client must never leak above
    // Infrastructure; the SBE codec is legitimately consumed by the
    // EntryPointListener (it decodes external-bot FIXP/SBE frames).
    private const string SdkGatewayClient = "B3.EntryPoint.Client";
    private const string SdkSbeCodec = "B3.EntryPoint.Sbe";

    private static readonly string[] AllInternalAssemblies =
    [
        Domain, Application, Infrastructure, Api, Host, EntryPointListener,
    ];

    // ── Domain: the base of the graph ──────────────────────────────────

    [Fact]
    public void Domain_DependsOnNoOtherInternalAssembly()
    {
        AssertInternalReferencesAreSubsetOf(Domain, allowed: []);
    }

    [Fact]
    public void Domain_DoesNotReferenceTheWireSdk()
    {
        AssertDoesNotReference(Domain, SdkGatewayClient, SdkSbeCodec);
    }

    // ── Application: orchestration, pure of the wire ───────────────────

    [Fact]
    public void Application_OnlyDependsOnDomainInternally()
    {
        AssertInternalReferencesAreSubsetOf(Application, allowed: [Domain]);
    }

    /// <summary>
    /// THE seam. <c>Application</c> holds risk, algos and persistence and
    /// must reach the venue only through <c>IExchangeGateway</c>. A direct
    /// reference to either SDK assembly means a wire type leaked into the
    /// orchestration layer — the regression this whole suite exists for.
    /// </summary>
    [Fact]
    public void Application_DoesNotReferenceTheWireSdk()
    {
        AssertDoesNotReference(Application, SdkGatewayClient, SdkSbeCodec);
    }

    // ── Api: presentation, only over Application + Domain ──────────────

    [Fact]
    public void Api_OnlyDependsOnApplicationAndDomainInternally()
    {
        // Forbids Infrastructure, Host and EntryPointListener — endpoint
        // code must reach concrete adapters through an Application-layer
        // port, never by re-adding the project reference (#188).
        AssertInternalReferencesAreSubsetOf(Api, allowed: [Domain, Application]);
    }

    [Fact]
    public void Api_DoesNotReferenceTheWireSdk()
    {
        AssertDoesNotReference(Api, SdkGatewayClient, SdkSbeCodec);
    }

    // ── EntryPointListener: SBE-codec consumer, not a gateway client ───

    [Fact]
    public void EntryPointListener_OnlyDependsOnApplicationAndDomainInternally()
    {
        AssertInternalReferencesAreSubsetOf(EntryPointListener, allowed: [Domain, Application]);
    }

    /// <summary>
    /// The listener decodes inbound external-bot frames with the SBE
    /// codec (<c>B3.EntryPoint.Sbe</c>) but must NOT take a dependency on
    /// the order-entry gateway client (<c>B3.EntryPoint.Client</c>) —
    /// outbound order flow belongs to Infrastructure behind the gateway.
    /// </summary>
    [Fact]
    public void EntryPointListener_DoesNotReferenceTheGatewayClient()
    {
        AssertDoesNotReference(EntryPointListener, SdkGatewayClient);
    }

    // ── Infrastructure: the sole gateway-client consumer ───────────────

    /// <summary>
    /// Positive guard: Infrastructure DOES reference the gateway client.
    /// Without this, a typo in <see cref="SdkGatewayClient"/> would make
    /// every "does not reference the SDK" assertion above pass vacuously.
    /// </summary>
    [Fact]
    public void Infrastructure_IsTheGatewayClientAdapter()
    {
        var refs = DirectReferencesOf(Infrastructure);
        Assert.True(
            refs.Contains(SdkGatewayClient),
            $"Expected '{Infrastructure}' to reference '{SdkGatewayClient}' (it is the " +
            "IExchangeGateway adapter). If the gateway moved, update this guard — but the " +
            "wire SDK must live behind exactly one internal assembly.");
    }

    /// <summary>
    /// Among the inspectable internal assemblies, Infrastructure is the
    /// only one allowed to reference the gateway client. (Host is the
    /// composition root and is excluded from inspection — it is allowed to
    /// reference everything and is an executable that cannot be referenced
    /// from a test project.)
    /// </summary>
    [Fact]
    public void OnlyInfrastructureReferencesTheGatewayClient()
    {
        var offenders = InspectableInternalAssemblies()
            .Where(name => name != Infrastructure)
            .Where(name => DirectReferencesOf(name).Contains(SdkGatewayClient))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Only '{Infrastructure}' may reference '{SdkGatewayClient}'. Offending " +
            $"assemblies: {string.Join(", ", offenders)}. Route wire access through " +
            "IExchangeGateway instead of taking a direct SDK dependency.");
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static void AssertInternalReferencesAreSubsetOf(string assemblySimpleName, string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var offenders = DirectReferencesOf(assemblySimpleName)
            .Where(r => AllInternalAssemblies.Contains(r, StringComparer.Ordinal))
            .Where(r => r != assemblySimpleName && !allowedSet.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        var allowedDisplay = allowed.Length == 0 ? "(none)" : string.Join(", ", allowed);
        Assert.True(
            offenders.Length == 0,
            $"'{assemblySimpleName}' may only depend internally on: {allowedDisplay}. " +
            $"Forbidden references found: {string.Join(", ", offenders)}. This breaks the " +
            "layered seam documented in AGENTS.md — introduce an Application-layer port " +
            "instead of an upward/sideways project reference.");
    }

    private static void AssertDoesNotReference(string assemblySimpleName, params string[] forbidden)
    {
        var refs = DirectReferencesOf(assemblySimpleName);
        var hits = forbidden.Where(refs.Contains).ToArray();
        Assert.True(
            hits.Length == 0,
            $"'{assemblySimpleName}' must not reference: {string.Join(", ", hits)}. The wire " +
            "SDK lives behind IExchangeGateway in Infrastructure; a direct reference here " +
            "couples a higher layer to wire types and breaks every SDK-bump in the future.");
    }

    private static HashSet<string> DirectReferencesOf(string assemblySimpleName)
    {
        var assembly = LoadProductionAssembly(assemblySimpleName);
        return assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The internal assemblies this suite can actually load and inspect —
    /// everything in <see cref="AllInternalAssemblies"/> except the Host
    /// executable, which is not referenced (see the project file comment).
    /// </summary>
    private static IEnumerable<string> InspectableInternalAssemblies()
        => AllInternalAssemblies.Where(name => name != Host);

    private static Assembly LoadProductionAssembly(string simpleName)
    {
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal));
        if (alreadyLoaded is not null)
            return alreadyLoaded;

        // ProjectReference copies the referenced assembly into this test's
        // output directory even when no type from it is used, so loading by
        // path is deterministic and free of fragile anchor-type lookups.
        var path = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
        Assert.True(
            File.Exists(path),
            $"Could not find production assembly '{simpleName}' at '{path}'. Add a " +
            "ProjectReference to it in B3.Trading.Architecture.Tests.csproj.");
        return Assembly.LoadFrom(path);
    }
}
