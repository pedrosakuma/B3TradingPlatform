using System.Globalization;

namespace B3.Trading.LoadTest;

/// <summary>
/// CLI options for the load-test harness. Parsed from a tiny hand-rolled
/// argument grammar so we don't pull in another package dependency for
/// what is a four-flag tool. Flag style mirrors
/// <c>BenchmarkDotNet</c>'s long-form (<c>--name value</c>) so muscle
/// memory carries between the bench and load harnesses.
/// </summary>
public sealed record LoadTestOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Target sustained submit rate in msg/s. <c>0</c> = unbounded
    /// (drive as fast as the producers can).
    /// </summary>
    public int RatePerSecond { get; init; }

    /// <summary>Number of concurrent submit producers.</summary>
    public int Concurrency { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Number of synthetic "bot" sinks subscribed to ER fan-out. Each
    /// observed publish is counted once per bot; latency is recorded once
    /// per submitted order against the first bot to deliver the matching
    /// ER (matches the RFC's §7.2 "bot-receives-ER" timing reference).
    /// </summary>
    public int Bots { get; init; } = 1;

    /// <summary>Directory for the WAL data dir. Defaults to a per-run temp dir.</summary>
    public string? WalDirectory { get; init; }

    /// <summary>Path to write the machine-readable results.json.</summary>
    public string? ResultsPath { get; init; }

    public bool ShowHelp { get; init; }

    public const string Usage = """
        Usage: dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- [options]

          --duration <span>     Steady-state run length (default: 30s).
          --warmup <span>       Pre-measurement warmup (default: 2s).
          --rate <int>          Target submit rate msg/s (0 = unbounded; default: 0).
          --concurrency <int>   Concurrent submit producers (default: Env.ProcessorCount).
          --bots <int>          Synthetic ER-receiving bot sinks (default: 1).
          --wal-dir <path>      WAL data dir (default: per-run temp dir, deleted on exit).
          --results <path>      Write results.json here (consumed by RFC §7.3 gates).
          --help                Show this help and exit.

        Time spans accept "30s", "2m", "500ms" or any TimeSpan literal.
        """;

    public static LoadTestOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var duration = TimeSpan.FromSeconds(30);
        var warmup = TimeSpan.FromSeconds(2);
        var rate = 0;
        var conc = Environment.ProcessorCount;
        var bots = 1;
        string? walDir = null;
        string? results = null;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--duration":
                    duration = ParseTimeSpan(NextValue(args, ref i, a));
                    break;
                case "--warmup":
                    warmup = ParseTimeSpan(NextValue(args, ref i, a));
                    break;
                case "--rate":
                    rate = ParseInt(NextValue(args, ref i, a), nonNegative: true);
                    break;
                case "--concurrency":
                    conc = ParseInt(NextValue(args, ref i, a), nonNegative: false);
                    if (conc < 1) throw new ArgumentException("--concurrency must be ≥1.");
                    break;
                case "--bots":
                    bots = ParseInt(NextValue(args, ref i, a), nonNegative: false);
                    if (bots < 1) throw new ArgumentException("--bots must be ≥1.");
                    break;
                case "--wal-dir":
                    walDir = NextValue(args, ref i, a);
                    break;
                case "--results":
                    results = NextValue(args, ref i, a);
                    break;
                default:
                    throw new ArgumentException($"unknown argument: '{a}' (use --help)");
            }
        }

        return new LoadTestOptions
        {
            Duration = duration,
            Warmup = warmup,
            RatePerSecond = rate,
            Concurrency = conc,
            Bots = bots,
            WalDirectory = walDir,
            ResultsPath = results,
            ShowHelp = help,
        };
    }

    private static string NextValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{flag} requires a value");
        return args[++i];
    }

    private static int ParseInt(string s, bool nonNegative)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new ArgumentException($"expected integer, got '{s}'");
        if (nonNegative && v < 0)
            throw new ArgumentException($"expected non-negative integer, got '{s}'");
        return v;
    }

    private static TimeSpan ParseTimeSpan(string s)
    {
        // Friendly suffixes: "500ms", "30s", "2m", "1h". Falls back to the
        // standard TimeSpan parser ("00:01:00") so existing config-style
        // strings work too.
        var trimmed = s.Trim();
        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMilliseconds(double.Parse(trimmed[..^2], CultureInfo.InvariantCulture));
        if (trimmed.EndsWith('s'))
            return TimeSpan.FromSeconds(double.Parse(trimmed[..^1], CultureInfo.InvariantCulture));
        if (trimmed.EndsWith('m'))
            return TimeSpan.FromMinutes(double.Parse(trimmed[..^1], CultureInfo.InvariantCulture));
        if (trimmed.EndsWith('h'))
            return TimeSpan.FromHours(double.Parse(trimmed[..^1], CultureInfo.InvariantCulture));
        return TimeSpan.Parse(trimmed, CultureInfo.InvariantCulture);
    }
}
