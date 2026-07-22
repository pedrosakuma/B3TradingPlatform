using System.Globalization;

namespace B3.Trading.Reconciliation;

/// <summary>
/// Parser for the fills section of the trading-host statement CSV
/// emitted by <c>GET /api/statement/{dayKey}.csv</c>. The CSV is
/// multi-section (positions, fills, fees, pnl, ir-day-trade) separated
/// by blank lines and re-headered for each section. We only care about
/// the fills section here; we identify it by header text and tolerate
/// the others changing shape.
/// </summary>
public static class TradingHostStatementParser
{
    private const string FillsHeader =
        "executionId,clOrdId,orderId,symbol,side,quantity,price,timestampUtc,subAccountId";

    public static IReadOnlyList<HostFillRow> ParseFills(string csvBody)
    {
        ArgumentNullException.ThrowIfNull(csvBody);
        var rows = new List<HostFillRow>();
        var inFills = false;
        var lineNo = 0;
        foreach (var raw in csvBody.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                inFills = false;
                continue;
            }
            if (string.Equals(NoWhitespace(line), FillsHeader, StringComparison.OrdinalIgnoreCase))
            {
                inFills = true;
                continue;
            }
            if (!inFills) continue;
            rows.Add(ParseFillRow(line, lineNo));
        }
        return rows;
    }

    private static string NoWhitespace(string s) =>
        string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    private static HostFillRow ParseFillRow(string line, int lineNo)
    {
        var cols = SplitCsv(line);
        if (cols.Count < 8)
            throw new InvalidDataException(
                $"Trading-host statement fill row at line {lineNo} has {cols.Count} columns, expected at least 8: '{line}'.");
        try
        {
            return new HostFillRow(
                ExecutionId: cols[0],
                ClOrdId: cols[1],
                OrderId: cols[2],
                Symbol: cols[3],
                Side: cols[4],
                Quantity: long.Parse(cols[5], CultureInfo.InvariantCulture),
                Price: decimal.Parse(cols[6], CultureInfo.InvariantCulture),
                TimestampUtc: DateTimeOffset.Parse(cols[7], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                SubAccountId: cols.Count >= 9 ? NullIfEmpty(cols[8]) : null);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                $"Malformed trading-host statement fill row at line {lineNo}: '{line}'. {ex.Message}", ex);
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static List<string> SplitCsv(string line)
    {
        // Statement CSV quotes only when the value needs it (commas /
        // double-quotes inside a field). Match the writer in
        // StatementEndpoints.RenderCsv: double-quote wrap + "" escape.
        var cols = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == ',')
                {
                    cols.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"' && sb.Length == 0)
                {
                    inQuotes = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        cols.Add(sb.ToString());
        return cols;
    }
}

public sealed record HostFillRow(
    string ExecutionId,
    string ClOrdId,
    string OrderId,
    string Symbol,
    string Side,
    long Quantity,
    decimal Price,
    DateTimeOffset TimestampUtc,
    string? SubAccountId);
