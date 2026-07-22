using System.Globalization;
using B3.Trading.Reconciliation;

// b3-reconcile — D+1 reconciliation tool (B3TradingPlatform#274).
//
// Compares the matching platform's EOD fills CSV drop
// (B3MatchingPlatform#330) against the trading-host's daily statement
// for a single firm + UTC date. Exits 0 when the (symbol, side)
// aggregates align (count + sum of quantity + sum of notional), 2 on
// any difference, 1 on argument / IO / integrity errors.
//
// Usage:
//   b3-reconcile \
//     --matching-fills-dir /var/post-trade/drops \
//     --channel 1 \
//     --date 2026-05-19 \
//     --firm FIRM01 \
//     --trading-host https://trading.local \
//     --auth-token "$TRADING_HOST_TOKEN"
//
// The trading-host statement is fetched from
// {tradingHostUrl}/api/statement/{date}.csv with a Bearer token.

try
{
    var parsed = Cli.Parse(args);
    if (parsed is null) return 0;

    var drop = MatchingFillsReader.Load(parsed.MatchingFillsDir, parsed.Channel, parsed.Date);
    Console.Error.WriteLine(
        $"loaded matching drop: rows={drop.RowCount} sha256={drop.Sha256[..12]}… generatedAt={drop.GeneratedAt:O}");

    using var http = new HttpClient { BaseAddress = new Uri(parsed.TradingHostUrl) };
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", parsed.AuthToken);
    var statementCsv = await http.GetStringAsync(
        $"/api/statement/{parsed.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv");
    var hostFills = TradingHostStatementParser.ParseFills(statementCsv);
    Console.Error.WriteLine($"loaded trading-host statement: fillRows={hostFills.Count}");

    var report = FillsComparator.Compare(drop.Rows, hostFills, parsed.Firm);
    Console.WriteLine(report.Render());
    return report.IsClean ? 0 : 2;
}
catch (CliException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine(Cli.UsageText);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

internal sealed class CliException(string message) : Exception(message);

internal sealed record CliArgs(
    string MatchingFillsDir,
    string Channel,
    DateOnly Date,
    string Firm,
    string TradingHostUrl,
    string AuthToken);

internal static class Cli
{
    public const string UsageText =
        "usage: b3-reconcile --matching-fills-dir <DIR> --channel <CH> --date <YYYY-MM-DD> " +
        "--firm <FIRM> --trading-host <URL> --auth-token <TOKEN>";

    public static CliArgs? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(UsageText);
            return null;
        }
        string? dir = null, channel = null, dateStr = null, firm = null, host = null, token = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--matching-fills-dir": dir = Next(args, ref i); break;
                case "--channel": channel = Next(args, ref i); break;
                case "--date": dateStr = Next(args, ref i); break;
                case "--firm": firm = Next(args, ref i); break;
                case "--trading-host": host = Next(args, ref i); break;
                case "--auth-token": token = Next(args, ref i); break;
                default: throw new CliException($"unknown argument: {args[i]}");
            }
        }
        if (dir is null || channel is null || dateStr is null || firm is null || host is null || token is null)
            throw new CliException("missing required argument(s).");
        if (!DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            throw new CliException($"invalid --date '{dateStr}'; expected YYYY-MM-DD.");
        return new CliArgs(dir, channel, date, firm, host, token);
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new CliException($"missing value for {args[i]}.");
        return args[++i];
    }
}
