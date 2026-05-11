using BenchmarkDotNet.Running;

namespace B3.Trading.Benchmarks;

/// <summary>
/// Entry point for the perf-hardening v0 bench harness (RFC §7.1, issue #195).
/// Delegates to <see cref="BenchmarkSwitcher"/> so callers can use the
/// standard CLI (e.g. <c>--filter '*DispatcherBench*'</c>, <c>--job Dry</c>,
/// <c>--list flat</c>). Sub-issue PRs in the perf epic (#194) extend this
/// project with before/after numbers; the README documents the runner spec.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
