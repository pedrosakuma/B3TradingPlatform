using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace B3.Trading.Reconciliation;

/// <summary>
/// Reader for the EOD fills CSV produced by the matching platform's
/// <c>EodFillsExporter</c> (B3MatchingPlatform#330 PR-1, columns frozen
/// by ADR-0001):
/// <code>tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm</code>
/// </summary>
public static class MatchingFillsReader
{
    /// <summary>
    /// Reads and validates the drop at
    /// <c>{dropRootDir}/{channel}/{yyyy-MM-dd}/fills.csv</c>. The
    /// <c>.done</c> sidecar must be present (it is the consumer-visible
    /// "ready" signal) and its declared SHA-256 must match the bytes of
    /// <c>fills.csv</c>.
    /// </summary>
    public static MatchingDropReadResult Load(string dropRootDir, string channel, DateOnly date)
    {
        ArgumentException.ThrowIfNullOrEmpty(dropRootDir);
        ArgumentException.ThrowIfNullOrEmpty(channel);
        var dir = Path.Combine(dropRootDir, channel, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var csvPath = Path.Combine(dir, "fills.csv");
        var donePath = Path.Combine(dir, "fills.csv.done");
        if (!File.Exists(donePath))
            throw new FileNotFoundException(
                $"Matching drop is not ready: missing {donePath}. The .done sidecar lands last in the atomic publish; consume only when it exists.",
                donePath);
        if (!File.Exists(csvPath))
            throw new FileNotFoundException($"Missing matching fills CSV at {csvPath}.", csvPath);

        var bytes = File.ReadAllBytes(csvPath);
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var doneJson = File.ReadAllText(donePath);
        var done = JsonSerializer.Deserialize<DoneSidecar>(doneJson, JsonOpts)
            ?? throw new InvalidDataException($"Unparsable .done sidecar at {donePath}.");
        if (!string.Equals(done.Sha256, actualSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"SHA-256 mismatch for {csvPath}: sidecar declares {done.Sha256} but file hashes to {actualSha}. " +
                "Drop is corrupt or being rewritten — refuse to reconcile.");

        var rows = ParseCsv(bytes).ToList();
        if (done.RowCount != rows.Count)
            throw new InvalidDataException(
                $"Row count mismatch for {csvPath}: sidecar declares {done.RowCount} but CSV has {rows.Count} data rows.");

        return new MatchingDropReadResult(rows, done.Sha256, done.RowCount, done.GeneratedAt);
    }

    /// <summary>
    /// Parses raw matching-CSV bytes. Public for unit tests that drive
    /// the comparator without touching the filesystem.
    /// </summary>
    public static IEnumerable<MatchingFillRow> ParseCsv(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var sr = new StreamReader(ms);
        var header = sr.ReadLine()
            ?? throw new InvalidDataException("Empty matching fills CSV.");
        if (!ColumnHeaderMatchesContract(header))
            throw new InvalidDataException(
                $"Unexpected matching fills header: '{header}'. Expected '{ContractHeader}' (ADR-0001).");

        string? line;
        var lineNo = 1;
        while ((line = sr.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0) continue;
            yield return ParseRow(line, lineNo);
        }
    }

    private const string ContractHeader =
        "tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm";

    private static bool ColumnHeaderMatchesContract(string header) =>
        // Allow whitespace variants in case the upstream emits "a, b, c".
        string.Equals(
            string.Concat(header.Where(c => !char.IsWhiteSpace(c))),
            ContractHeader,
            StringComparison.OrdinalIgnoreCase);

    private static MatchingFillRow ParseRow(string line, int lineNo)
    {
        var cols = line.Split(',');
        if (cols.Length != 10)
            throw new InvalidDataException(
                $"Matching fills row at line {lineNo} has {cols.Length} columns, expected 10: '{line}'.");
        try
        {
            return new MatchingFillRow(
                TradeId: cols[0],
                TimestampUtc: DateTimeOffset.Parse(cols[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                Symbol: cols[2],
                AggressorSide: cols[3],
                Quantity: long.Parse(cols[4], CultureInfo.InvariantCulture),
                Price: decimal.Parse(cols[5], CultureInfo.InvariantCulture),
                BuyClOrdId: cols[6],
                SellClOrdId: cols[7],
                BuyFirm: cols[8],
                SellFirm: cols[9]);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                $"Malformed matching fills row at line {lineNo}: '{line}'. {ex.Message}", ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record DoneSidecar(int RowCount, string Sha256, DateTimeOffset GeneratedAt);
}

public sealed record MatchingFillRow(
    string TradeId,
    DateTimeOffset TimestampUtc,
    string Symbol,
    string AggressorSide,
    long Quantity,
    decimal Price,
    string BuyClOrdId,
    string SellClOrdId,
    string BuyFirm,
    string SellFirm);

public sealed record MatchingDropReadResult(
    IReadOnlyList<MatchingFillRow> Rows,
    string Sha256,
    int RowCount,
    DateTimeOffset GeneratedAt);
