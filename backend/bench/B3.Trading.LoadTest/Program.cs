using System.Globalization;
using System.Text;

using B3.Trading.LoadTest;

namespace B3.Trading.LoadTest;

/// <summary>
/// Entry point for the perf-hardening v0 macro/load harness (RFC §7.2,
/// issue #207). Drives the in-process REST submit → WAL durable → bot
/// ER receive end-to-end pipeline at a configurable rate/concurrency
/// for a configurable duration, and emits throughput + latency
/// percentiles to stdout and (optionally) a JSON results file consumed
/// by the gates in RFC §7.3.
///
/// <para>
/// This harness is the macro complement to the BenchmarkDotNet micro
/// suite under <c>backend/bench/B3.Trading.Benchmarks</c> (PR #213). It
/// is excluded from CI by virtue of being a non-test Exe with
/// <c>IsPackable=false</c>; run on demand via:
/// <code>
/// dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- \
///     --duration 60s --rate 50000 --concurrency 8 --bots 1
/// </code>
/// </para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        LoadTestOptions opts;
        try
        {
            opts = LoadTestOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(LoadTestOptions.Usage);
            return 2;
        }

        if (opts.ShowHelp)
        {
            Console.WriteLine(LoadTestOptions.Usage);
            return 0;
        }

        Console.WriteLine("== B3.Trading.LoadTest (RFC §7.2 / issue #207) ==");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  duration       : {opts.Duration}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  warmup         : {opts.Warmup}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  target rate    : {opts.RatePerSecond:N0} msg/s (0 = unbounded)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  concurrency    : {opts.Concurrency}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  bots (sinks)   : {opts.Bots}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  wal directory  : {opts.WalDirectory ?? "<temp>"}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  results file   : {opts.ResultsPath ?? "<stdout only>"}"));
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            // Best-effort cooperative shutdown; second Ctrl-C exits.
            if (!cts.IsCancellationRequested) cts.Cancel();
        };

        await using var rig = await LoadTestRig.BootAsync(opts, cts.Token).ConfigureAwait(false);
        var report = await rig.RunAsync(cts.Token).ConfigureAwait(false);

        var json = report.ToJson(opts);
        if (opts.ResultsPath is { } path)
        {
            await File.WriteAllTextAsync(path, json, Encoding.UTF8, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[ok] results written to {path}"));
        }

        Console.WriteLine();
        Console.WriteLine(report.Format(opts));
        return 0;
    }
}
