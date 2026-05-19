using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using B3.Trading.Reconciliation;

namespace B3.Trading.Reconciliation.Tests;

public class MatchingFillsReaderTests
{
    [Fact]
    public void Parses_canonical_contract_csv()
    {
        var csv = "tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm\n" +
                  "T1,2026-05-19T10:00:00.123456Z,PETR4,Buy,100,30.55,B1,S1,FIRM01,FIRM02\n" +
                  "T2,2026-05-19T10:00:05.000000Z,VALE3,Sell,50,72.10,B2,S2,FIRM03,FIRM01\n";
        var rows = MatchingFillsReader.ParseCsv(Encoding.UTF8.GetBytes(csv)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("T1", rows[0].TradeId);
        Assert.Equal("PETR4", rows[0].Symbol);
        Assert.Equal(100, rows[0].Quantity);
        Assert.Equal(30.55m, rows[0].Price);
        Assert.Equal("FIRM02", rows[0].SellFirm);
        Assert.Equal("FIRM01", rows[1].SellFirm);
    }

    [Fact]
    public void Rejects_wrong_header()
    {
        var csv = "tradeId,ts,symbol\nT1,2026-05-19T10:00:00Z,PETR4\n";
        Assert.Throws<InvalidDataException>(() =>
            MatchingFillsReader.ParseCsv(Encoding.UTF8.GetBytes(csv)).ToList());
    }

    [Fact]
    public void Load_validates_sha256_against_done_sidecar()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "1", "2026-05-19");
        Directory.CreateDirectory(dir);
        var csv = "tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm\n" +
                  "T1,2026-05-19T10:00:00Z,PETR4,Buy,100,30.55,B1,S1,FIRM01,FIRM02\n";
        var bytes = Encoding.UTF8.GetBytes(csv);
        File.WriteAllBytes(Path.Combine(dir, "fills.csv"), bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var done = JsonSerializer.Serialize(new { rowCount = 1, sha256 = sha, generatedAt = "2026-05-19T18:00:00Z" });
        File.WriteAllText(Path.Combine(dir, "fills.csv.done"), done);

        var result = MatchingFillsReader.Load(root, "1", new DateOnly(2026, 5, 19));
        Assert.Single(result.Rows);
        Assert.Equal(sha, result.Sha256);
    }

    [Fact]
    public void Load_rejects_mismatched_sha()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "1", "2026-05-19");
        Directory.CreateDirectory(dir);
        var bytes = Encoding.UTF8.GetBytes(
            "tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm\n" +
            "T1,2026-05-19T10:00:00Z,PETR4,Buy,100,30.55,B1,S1,FIRM01,FIRM02\n");
        File.WriteAllBytes(Path.Combine(dir, "fills.csv"), bytes);
        var done = JsonSerializer.Serialize(new { rowCount = 1, sha256 = "deadbeef", generatedAt = "2026-05-19T18:00:00Z" });
        File.WriteAllText(Path.Combine(dir, "fills.csv.done"), done);

        Assert.Throws<InvalidDataException>(() =>
            MatchingFillsReader.Load(root, "1", new DateOnly(2026, 5, 19)));
    }

    [Fact]
    public void Load_refuses_when_done_sidecar_absent()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "1", "2026-05-19");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fills.csv"), "header\n");
        Assert.Throws<FileNotFoundException>(() =>
            MatchingFillsReader.Load(root, "1", new DateOnly(2026, 5, 19)));
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"b3-recon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public class TradingHostStatementParserTests
{
    [Fact]
    public void Extracts_fills_section_and_ignores_other_sections()
    {
        // Mirrors the multi-section layout in StatementEndpoints.RenderCsv.
        var csv = string.Join("\r\n", new[]
        {
            "symbol,netQty,avgPrice,subAccountId",
            "PETR4,100,30.55,",
            "",
            "executionId,clOrdId,orderId,symbol,side,quantity,price,timestampUtc,subAccountId",
            "1001:100,1001,1001,PETR4,Buy,100,30.55,2026-05-19T10:00:00Z,",
            "1002:50,1002,1002,VALE3,Sell,50,72.10,2026-05-19T10:00:05Z,",
            "",
            "feeType,total",
            "emoluments,12.34",
            "totalFees,12.34",
            "",
        });
        var fills = TradingHostStatementParser.ParseFills(csv);
        Assert.Equal(2, fills.Count);
        Assert.Equal("PETR4", fills[0].Symbol);
        Assert.Equal(100, fills[0].Quantity);
        Assert.Equal(30.55m, fills[0].Price);
        Assert.Equal("Sell", fills[1].Side);
    }

    [Fact]
    public void Handles_quoted_fields_with_commas()
    {
        var csv = "executionId,clOrdId,orderId,symbol,side,quantity,price,timestampUtc,subAccountId\r\n" +
                  "\"1001:100\",1001,1001,\"PETR,4\",Buy,100,30.55,2026-05-19T10:00:00Z,";
        var fills = TradingHostStatementParser.ParseFills(csv);
        Assert.Single(fills);
        Assert.Equal("PETR,4", fills[0].Symbol);
    }
}

public class FillsComparatorTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.Parse("2026-05-19T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Clean_alignment_when_aggregates_match()
    {
        var matching = new List<MatchingFillRow>
        {
            new("T1", Ts, "PETR4", "Buy", 100, 30.55m, "B1", "S1", "FIRM01", "FIRM02"),
            new("T2", Ts.AddSeconds(5), "PETR4", "Sell", 50, 30.60m, "B2", "S2", "FIRM03", "FIRM01"),
        };
        // FIRM01 is buyer in T1 (Buy 100@30.55) and seller in T2 (Sell 50@30.60).
        var host = new List<HostFillRow>
        {
            new("1001:100", "1001", "1001", "PETR4", "Buy", 100, 30.55m, Ts, null),
            new("1002:50", "1002", "1002", "PETR4", "Sell", 50, 30.60m, Ts.AddSeconds(5), null),
        };
        var report = FillsComparator.Compare(matching, host, "FIRM01");
        Assert.True(report.IsClean, report.Render());
        Assert.Equal(2, report.MatchingRowCount);
        Assert.Equal(2, report.HostRowCount);
    }

    [Fact]
    public void Internal_cross_expands_into_two_host_rows()
    {
        // Matching emits ONE row for an internal cross — FIRM01 buyer
        // and FIRM01 seller. Trading-host writes two separate fill
        // ERs (one per ClOrdId). The projection must mirror that.
        var matching = new List<MatchingFillRow>
        {
            new("T1", Ts, "PETR4", "Buy", 100, 30.55m, "B1", "S1", "FIRM01", "FIRM01"),
        };
        var host = new List<HostFillRow>
        {
            new("B1:100", "B1", "B1", "PETR4", "Buy", 100, 30.55m, Ts, null),
            new("S1:100", "S1", "S1", "PETR4", "Sell", 100, 30.55m, Ts, null),
        };
        var report = FillsComparator.Compare(matching, host, "FIRM01");
        Assert.True(report.IsClean, report.Render());
        Assert.Equal(2, report.MatchingRowCount); // projection expanded
        Assert.Equal(2, report.HostRowCount);
    }

    [Fact]
    public void Quantity_drift_surfaces_as_per_bucket_diff()
    {
        var matching = new List<MatchingFillRow>
        {
            new("T1", Ts, "PETR4", "Buy", 100, 30.55m, "B1", "S1", "FIRM01", "FIRM02"),
        };
        // Host recorded only 80 — the missing 20 is the kind of drift
        // this tool is designed to surface.
        var host = new List<HostFillRow>
        {
            new("1001:80", "1001", "1001", "PETR4", "Buy", 80, 30.55m, Ts, null),
        };
        var report = FillsComparator.Compare(matching, host, "FIRM01");
        Assert.False(report.IsClean);
        Assert.Single(report.Diffs);
        var diff = report.Diffs[0];
        Assert.Equal("PETR4", diff.Symbol);
        Assert.Equal("Buy", diff.Side);
        Assert.Equal(100, diff.Matching.TotalQty);
        Assert.Equal(80, diff.Host.TotalQty);
    }

    [Fact]
    public void Rows_for_other_firms_are_filtered_out()
    {
        var matching = new List<MatchingFillRow>
        {
            new("T1", Ts, "PETR4", "Buy", 100, 30.55m, "B1", "S1", "FIRM02", "FIRM03"),
        };
        var report = FillsComparator.Compare(matching, Array.Empty<HostFillRow>(), "FIRM01");
        Assert.True(report.IsClean, report.Render());
        Assert.Equal(0, report.MatchingRowCount);
    }

    [Fact]
    public void Missing_bucket_on_host_side_surfaces_as_diff()
    {
        var matching = new List<MatchingFillRow>
        {
            new("T1", Ts, "VALE3", "Sell", 50, 72.10m, "B1", "S1", "FIRM02", "FIRM01"),
        };
        var report = FillsComparator.Compare(matching, Array.Empty<HostFillRow>(), "FIRM01");
        Assert.False(report.IsClean);
        Assert.Single(report.Diffs);
        Assert.Equal(0, report.Diffs[0].Host.Count);
        Assert.Equal(1, report.Diffs[0].Matching.Count);
    }
}
