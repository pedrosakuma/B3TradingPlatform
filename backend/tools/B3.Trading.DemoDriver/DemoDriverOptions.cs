namespace B3.Trading.DemoDriver;

/// <summary>
/// Configuration for the demo-driver process. All values come from environment
/// variables (DEMO_*). Defaults are tuned to "looks alive in the trader UI
/// without tripping any risk limits at default settings".
/// </summary>
internal sealed class DemoDriverOptions
{
    public string BackendUrl { get; init; } = "http://trading-host:5000";

    /// <summary>Bots that submit orders. Format: "username:password" CSV.</summary>
    public IReadOnlyList<BotCredential> UserBots { get; init; } = Array.Empty<BotCredential>();

    /// <summary>Single admin used by the InjectorWorker. Required when Mode == Simulator.</summary>
    public BotCredential? Admin { get; init; }

    /// <summary>Symbols + reference prices the bots quote around. Format: "SYMBOL:px" CSV.</summary>
    public IReadOnlyList<SymbolRef> Symbols { get; init; } = Array.Empty<SymbolRef>();

    /// <summary>Per-bot order submission rate.</summary>
    public double SubmitRateHz { get; init; } = 0.5;

    /// <summary>Inject (Fill / PartialFill) rate across all bots.</summary>
    public double InjectRateHz { get; init; } = 0.3;

    /// <summary>auto-detect | simulator-inject | submit-only.</summary>
    public string Mode { get; init; } = "auto-detect";

    /// <summary>Cap working orders per bot — pause submitting once reached.</summary>
    public int MaxOpenOrdersPerBot { get; init; } = 50;

    public static DemoDriverOptions FromEnvironment()
    {
        return new DemoDriverOptions
        {
            BackendUrl = GetEnv("DEMO_BACKEND_URL", "http://trading-host:5000"),
            UserBots = ParseBots(GetEnv("DEMO_USER_BOTS", "bot-clientA:demopass,bot-clientB:demopass")),
            Admin = ParseBot(GetEnv("DEMO_ADMIN", "demo-admin:demopass")),
            Symbols = ParseSymbols(GetEnv("DEMO_SYMBOLS", "PETR4:32.50,VALE3:65.00,ITUB4:30.00")),
            SubmitRateHz = double.Parse(GetEnv("DEMO_SUBMIT_RATE_HZ", "0.5"), System.Globalization.CultureInfo.InvariantCulture),
            InjectRateHz = double.Parse(GetEnv("DEMO_INJECT_RATE_HZ", "0.3"), System.Globalization.CultureInfo.InvariantCulture),
            Mode = GetEnv("DEMO_MODE", "auto-detect"),
            MaxOpenOrdersPerBot = int.Parse(GetEnv("DEMO_MAX_OPEN_ORDERS", "50"), System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string GetEnv(string key, string fallback)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static IReadOnlyList<BotCredential> ParseBots(string csv)
    {
        var list = new List<BotCredential>();
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = ParseBot(raw);
            if (parsed is not null) list.Add(parsed);
        }
        return list;
    }

    private static BotCredential? ParseBot(string raw)
    {
        var parts = raw.Split(':', 2);
        if (parts.Length != 2) return null;
        return new BotCredential(parts[0].Trim(), parts[1].Trim());
    }

    private static IReadOnlyList<SymbolRef> ParseSymbols(string csv)
    {
        var list = new List<SymbolRef>();
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(':', 2);
            if (parts.Length != 2) continue;
            if (!decimal.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var px))
                continue;
            list.Add(new SymbolRef(parts[0].Trim().ToUpperInvariant(), px));
        }
        return list;
    }
}

internal sealed record BotCredential(string Username, string Password);

internal sealed record SymbolRef(string Symbol, decimal ReferencePrice);
