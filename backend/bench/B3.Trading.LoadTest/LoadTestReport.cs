using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace B3.Trading.LoadTest;

/// <summary>
/// Aggregated outcome of a load-test run. Exposes both a human-readable
/// formatter (for stdout / PR bodies) and a JSON serialiser shaped to
/// the gates in RFC §7.3 — sub-issues P3-P11 read these numbers as the
/// "before / after" reference for their own throughput + latency
/// deltas.
/// </summary>
public sealed record LoadTestReport
{
    public required long SubmittedCount { get; init; }
    public required long AcceptedCount { get; init; }
    public required long RejectedCount { get; init; }
    public required long ErsApplied { get; init; }
    public required long ErDispatchFailures { get; init; }
    public required long PublishCount { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required long[] LatencyTicks { get; init; }
    public required long TicksPerSecond { get; init; }

    public double SubmitsPerSecond => ElapsedSeconds > 0 ? AcceptedCount / ElapsedSeconds : 0;
    public double ErsPerSecond => ElapsedSeconds > 0 ? PublishCount / ElapsedSeconds : 0;

    public LatencyPercentiles Percentiles()
    {
        if (LatencyTicks.Length == 0)
            return LatencyPercentiles.Empty;

        // The harness writes into a pre-sized array indexed by a free
        // running counter; the active prefix is dense from index 0 so
        // we sort the live span only.
        var copy = (long[])LatencyTicks.Clone();
        Array.Sort(copy);
        return new LatencyPercentiles(
            CountSamples: copy.LongLength,
            P50Ns: TicksToNs(Quantile(copy, 0.50)),
            P95Ns: TicksToNs(Quantile(copy, 0.95)),
            P99Ns: TicksToNs(Quantile(copy, 0.99)),
            P999Ns: TicksToNs(Quantile(copy, 0.999)),
            MaxNs: TicksToNs(copy[^1]));
    }

    private long TicksToNs(long ticks) =>
        TicksPerSecond > 0 ? (long)(ticks * 1_000_000_000d / TicksPerSecond) : 0;

    private static long Quantile(long[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Clamp(Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    public string Format(LoadTestOptions opts)
    {
        var p = Percentiles();
        var sb = new StringBuilder();
        sb.AppendLine("== Results ==");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  elapsed              : {ElapsedSeconds:N3} s");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  submits attempted    : {SubmittedCount:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  submits accepted     : {AcceptedCount:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  submits rejected     : {RejectedCount:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  ERs dispatched (WAL) : {ErsApplied:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  ERs published (sink) : {PublishCount:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  ER dispatch failures : {ErDispatchFailures:N0}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  throughput  submit  : {SubmitsPerSecond,12:N0} msg/s");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  throughput  ER     : {ErsPerSecond,12:N0} msg/s");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  e2e latency samples : {p.CountSamples:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  e2e p50            : {FormatNs(p.P50Ns)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  e2e p95            : {FormatNs(p.P95Ns)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  e2e p99            : {FormatNs(p.P99Ns)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  e2e p99.9          : {FormatNs(p.P999Ns)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  e2e max            : {FormatNs(p.MaxNs)}");
        sb.AppendLine();
        var gateMet = SubmitsPerSecond >= 100_000 && p.P99Ns <= 5_000_000;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  RFC §7.3 composite gate (≥100k msg/s, p99 e2e ≤5ms) : {(gateMet ? "MET" : "NOT MET — expected on baseline pre P3-P11")}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  config: rate={opts.RatePerSecond:N0} concurrency={opts.Concurrency} bots={opts.Bots} duration={opts.Duration} warmup={opts.Warmup}");
        return sb.ToString();
    }

    public string ToJson(LoadTestOptions opts)
    {
        var p = Percentiles();
        var doc = new
        {
            schema = "b3-loadtest/v1",
            rfc = "perf-hardening-v0 §7.2 (issue #207)",
            machine = new
            {
                processor_count = Environment.ProcessorCount,
                os = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString(),
                ticks_per_second = TicksPerSecond,
                stopwatch_high_res = Stopwatch.IsHighResolution,
            },
            config = new
            {
                duration_s = opts.Duration.TotalSeconds,
                warmup_s = opts.Warmup.TotalSeconds,
                rate_msg_per_s = opts.RatePerSecond,
                concurrency = opts.Concurrency,
                bots = opts.Bots,
                wal_dir = opts.WalDirectory,
            },
            counters = new
            {
                submitted = SubmittedCount,
                accepted = AcceptedCount,
                rejected = RejectedCount,
                ers_dispatched = ErsApplied,
                ers_published = PublishCount,
                er_dispatch_failures = ErDispatchFailures,
            },
            throughput = new
            {
                submit_msg_per_s = SubmitsPerSecond,
                er_msg_per_s = ErsPerSecond,
                elapsed_s = ElapsedSeconds,
            },
            latency_e2e_ns = new
            {
                count = p.CountSamples,
                p50 = p.P50Ns,
                p95 = p.P95Ns,
                p99 = p.P99Ns,
                p999 = p.P999Ns,
                max = p.MaxNs,
            },
        };
        return JsonSerializer.Serialize(doc,
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatNs(long ns)
    {
        if (ns >= 1_000_000) return string.Create(CultureInfo.InvariantCulture, $"{ns / 1_000_000d,8:N3} ms");
        if (ns >= 1_000) return string.Create(CultureInfo.InvariantCulture, $"{ns / 1_000d,8:N3} µs");
        return string.Create(CultureInfo.InvariantCulture, $"{ns,8:N0} ns");
    }
}

public sealed record LatencyPercentiles(
    long CountSamples,
    long P50Ns,
    long P95Ns,
    long P99Ns,
    long P999Ns,
    long MaxNs)
{
    public static readonly LatencyPercentiles Empty =
        new(0, 0, 0, 0, 0, 0);
}
