namespace B3.Trading.Architecture.Tests;

public sealed class ProbeContractTests
{
    [Fact]
    public void ContainerHealthcheck_UsesProcessLiveness()
    {
        var dockerfile = ReadRepoFile("backend", "src", "B3.Trading.Host", "Dockerfile");

        Assert.Contains("http://127.0.0.1:5000/live", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("http://127.0.0.1:5000/ready", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessSmokes_UseLive_WhileKubernetesReadinessUsesReady()
    {
        var workflow = ReadRepoFile(".github", "workflows", "docker.yml");
        Assert.Contains("http://127.0.0.1:18080/live", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("http://127.0.0.1:18080/ready", workflow, StringComparison.Ordinal);

        var conformanceEntrypoint = ReadRepoFile("docker", "conformance", "entrypoint.sh");
        Assert.Contains("live_url=${B3T_LIVE_URL:-$base_url/live}", conformanceEntrypoint, StringComparison.Ordinal);
        Assert.Contains("ready_url=${B3T_READY_URL:-$base_url/ready}", conformanceEntrypoint, StringComparison.Ordinal);
        Assert.Contains("health_json == *'\"mode\":\"Unavailable\"'*", conformanceEntrypoint, StringComparison.Ordinal);

        var realConformance = ReadRepoFile("docker", "docker-compose.real-conformance.yml");
        Assert.Contains("B3T_REQUIRE_READY: \"true\"", realConformance, StringComparison.Ordinal);

        var chartValues = ReadRepoFile("charts", "b3-trading-host", "values.yaml");
        Assert.Contains("readiness:\n    path: /ready", chartValues, StringComparison.Ordinal);
        Assert.Contains("liveness:\n    path: /live", chartValues, StringComparison.Ordinal);

        var demoModeState = ReadRepoFile(
            "backend", "tools", "B3.Trading.DemoDriver", "DemoModeState.cs");
        Assert.Contains("probeClient.IsReadyAsync", demoModeState, StringComparison.Ordinal);
        var demoClient = ReadRepoFile(
            "backend", "tools", "B3.Trading.DemoDriver", "TradingClient.cs");
        Assert.Contains("_http.GetAsync(\"/ready\", ct)", demoClient, StringComparison.Ordinal);

        var e2eWorkflow = ReadRepoFile(".github", "workflows", "e2e-smoke.yml");
        Assert.Contains("Wait for order-ingress readiness", e2eWorkflow, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:5000/ready", e2eWorkflow, StringComparison.Ordinal);

        var chaosScript = ReadRepoFile("scripts", "chaos", "run-chaos-drill.sh");
        Assert.Contains("initial /ready before scenario", chaosScript, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var path = Path.Combine([RepoRoot(), .. segments]);
        Assert.True(File.Exists(path), $"Expected repository file at '{path}'.");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "B3TradingPlatform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"Could not locate repository root from '{AppContext.BaseDirectory}'.");
    }
}
