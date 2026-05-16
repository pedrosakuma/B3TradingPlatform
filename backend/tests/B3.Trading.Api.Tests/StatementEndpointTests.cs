using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using B3.Trading.Application;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q2.5 (#272). HTTP integration coverage for the daily statement
/// endpoints (JSON + CSV). Mirrors the
/// <c>HistoryEndpointTests</c> shape: per-test data dir + mock
/// EntryPoint client so fill ERs land on the WAL synchronously,
/// JWT-auth via the standard test login.
/// </summary>
public class StatementEndpointTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "b3-statement-tests-" + Guid.NewGuid().ToString("N"));

    private IDictionary<string, string?> Overrides() => new Dictionary<string, string?>
    {
        ["Trading:Exchange:Mode"] = "Mock",
        ["Trading:Exchange:AllowErInjection"] = "true",
        ["Trading:Persistence:Enabled"] = "true",
        ["Trading:Persistence:DataDirectory"] = _dataDir,
        ["Trading:Persistence:FirmId"] = "test",
        ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // Best-effort tmp cleanup.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var resp = await http.GetAsync("/statement");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task EmptyDay_ReturnsEmptyStatementWithZeroTotals()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var req = Bearer(HttpMethod.Get, "/statement", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).ToString("yyyy-MM-dd"),
            dto.GetProperty("dayKey").GetString());
        Assert.Empty(dto.GetProperty("positions").EnumerateArray());
        Assert.Empty(dto.GetProperty("fills").EnumerateArray());
        Assert.Empty(dto.GetProperty("fees").EnumerateArray());
        Assert.Equal(0m, dto.GetProperty("feesTotal").GetDecimal());
        Assert.Equal(0m, dto.GetProperty("pnl").GetProperty("realizedGross").GetDecimal());
        Assert.Equal(0m, dto.GetProperty("pnl").GetProperty("realizedNet").GetDecimal());
        Assert.True(dto.GetProperty("irDayTrade").GetProperty("informationalOnly").GetBoolean());
        Assert.True(dto.GetProperty("irDayTrade").GetProperty("notCollected").GetBoolean());
        Assert.Equal(0m, dto.GetProperty("irDayTrade").GetProperty("totalTax").GetDecimal());
    }

    [Fact]
    public async Task WithFills_TotalsMatchManualSum()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var c1 = ulong.Parse(await SubmitBuy(http, token, qty: 100, price: 30m));
        await InjectFill(http, adminToken, c1, qty: 100, price: 30m);

        var dto = await GetStatement(http, token);
        Assert.NotEmpty(dto.GetProperty("fills").EnumerateArray());
        var fill = Assert.Single(dto.GetProperty("fills").EnumerateArray());
        Assert.Equal(100L, fill.GetProperty("quantity").GetInt64());
        Assert.Equal(30m, fill.GetProperty("price").GetDecimal());
        Assert.Equal("Buy", fill.GetProperty("side").GetString());

        // After a single 100-share buy the live position is 100 @ 30 —
        // the projection MUST use the live PositionKeeper for today.
        var pos = Assert.Single(dto.GetProperty("positions").EnumerateArray());
        Assert.Equal(100L, pos.GetProperty("netQty").GetInt64());
        Assert.Equal(30m, pos.GetProperty("avgPrice").GetDecimal());

        // FeeKeeper writes a FeeAccruedEvent per fill → feesTotal > 0.
        var feesTotal = dto.GetProperty("feesTotal").GetDecimal();
        Assert.True(feesTotal > 0m, "expected non-zero feesTotal after a fill");
        var pnl = dto.GetProperty("pnl");
        Assert.Equal(pnl.GetProperty("realizedGross").GetDecimal() - feesTotal,
            pnl.GetProperty("realizedNet").GetDecimal());
    }

    [Fact]
    public async Task IntradayDayTrade_AppliesTwentyPercentInformationalTax()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var buyId = ulong.Parse(await SubmitBuy(http, token, qty: 100, price: 30m));
        await InjectFill(http, adminToken, buyId, qty: 100, price: 30m);
        var sellId = ulong.Parse(await SubmitSell(http, token, qty: 100, price: 32m));
        await InjectFill(http, adminToken, sellId, qty: 100, price: 32m);

        var dto = await GetStatement(http, token);
        var perSymbol = dto.GetProperty("irDayTrade").GetProperty("perSymbol").EnumerateArray().ToList();
        var ir = Assert.Single(perSymbol);
        Assert.Equal("PETR4", ir.GetProperty("symbol").GetString());
        Assert.Equal(100L, ir.GetProperty("qtyMatched").GetInt64());
        Assert.Equal(200m, ir.GetProperty("grossProfit").GetDecimal());
        Assert.Equal(200m, ir.GetProperty("taxableProfit").GetDecimal());
        Assert.Equal(40.00m, ir.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(40.00m, dto.GetProperty("irDayTrade").GetProperty("totalTax").GetDecimal());
    }

    [Fact]
    public async Task NoDayTrade_ReturnsZeroIrTax()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        // Buy only — no offsetting same-day sell.
        var buyId = ulong.Parse(await SubmitBuy(http, token, qty: 50, price: 30m));
        await InjectFill(http, adminToken, buyId, qty: 50, price: 30m);

        var dto = await GetStatement(http, token);
        Assert.Empty(dto.GetProperty("irDayTrade").GetProperty("perSymbol").EnumerateArray());
        Assert.Equal(0m, dto.GetProperty("irDayTrade").GetProperty("totalTax").GetDecimal());
    }

    [Fact]
    public async Task CsvEndpoint_IsParseableAndCarriesUtf8Bom()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var buyId = ulong.Parse(await SubmitBuy(http, token, qty: 100, price: 30m));
        await InjectFill(http, adminToken, buyId, qty: 100, price: 30m);

        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).ToString("yyyy-MM-dd");
        var req = Bearer(HttpMethod.Get, $"/statement/{day}.csv", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", resp.Content.Headers.ContentType?.CharSet);
        Assert.Equal($"statement-{day}.csv", resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 3, "csv payload is unexpectedly short");
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        // Strip the BOM then parse each section with the standard
        // System library's TextFieldParser-equivalent: a simple
        // string-splitter is enough for the deterministic data we
        // emit (no embedded commas/newlines in the test fixture).
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("# positions", text);
        Assert.Contains("# fills", text);
        Assert.Contains("# fees", text);
        Assert.Contains("# pnl-summary", text);
        Assert.Contains("# ir-day-trade (informational)", text);

        // Round-trip the fills section through a generic CSV reader to
        // prove it's parseable. We isolate the block following
        // "# fills" until the next blank line and feed it through
        // System.Text-based splitting (Spec calls out "standard reader" —
        // the format is RFC4180-ish so anything compliant works).
        var fillsBlock = ExtractSection(text, "# fills");
        var rows = ParseCsv(fillsBlock);
        Assert.True(rows.Count >= 2, "expected header + at least one row");
        // Header columns
        Assert.Equal("executionId", rows[0][0]);
        Assert.Equal("clOrdId", rows[0][1]);
        Assert.Equal("symbol", rows[0][3]);
        Assert.Equal("side", rows[0][4]);
        Assert.Equal("quantity", rows[0][5]);
        Assert.Equal("price", rows[0][6]);
        // Row[1] is the one and only fill — PETR4 Buy 100 @ 30.
        Assert.Equal("PETR4", rows[1][3]);
        Assert.Equal("Buy", rows[1][4]);
        Assert.Equal("100", rows[1][5]);
        Assert.Equal("30", rows[1][6]);
    }

    [Fact]
    public async Task EndpointScope_TraderOnlySeesOwnEndClient()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var aliceToken = await f.LoginAsync(http);
        var bobToken = await f.LoginAsync(http, user: "bob");
        var adminToken = await f.LoginAsync(http, "admin");

        var aliceBuy = ulong.Parse(await SubmitBuy(http, aliceToken, qty: 10, price: 30m));
        await InjectFill(http, adminToken, aliceBuy, qty: 10, price: 30m);
        var bobBuy = ulong.Parse(await SubmitBuy(http, bobToken, qty: 20, price: 30m));
        await InjectFill(http, adminToken, bobBuy, qty: 20, price: 30m);

        var aliceDto = await GetStatement(http, aliceToken);
        var aliceFills = aliceDto.GetProperty("fills").EnumerateArray().ToList();
        var aliceFill = Assert.Single(aliceFills);
        Assert.Equal(10L, aliceFill.GetProperty("quantity").GetInt64());
        Assert.DoesNotContain(aliceFills, fill => fill.GetProperty("clOrdId").GetString() == bobBuy.ToString());

        var bobDto = await GetStatement(http, bobToken);
        var bobFills = bobDto.GetProperty("fills").EnumerateArray().ToList();
        var bobFill = Assert.Single(bobFills);
        Assert.Equal(20L, bobFill.GetProperty("quantity").GetInt64());
    }

    [Fact]
    public async Task FutureDayKey_Returns404()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var future = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(7).ToString("yyyy-MM-dd");
        var req = Bearer(HttpMethod.Get, $"/statement/{future}", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task MalformedDayKey_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var req = Bearer(HttpMethod.Get, "/statement/not-a-date", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<JsonElement> GetStatement(HttpClient http, string token, string? dayKey = null)
    {
        var url = dayKey is null ? "/statement" : $"/statement/{dayKey}";
        var req = Bearer(HttpMethod.Get, url, token);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<string> SubmitBuy(HttpClient http, string token, int qty, decimal price) =>
        SubmitOrder(http, token, "Buy", qty, price);

    private static Task<string> SubmitSell(HttpClient http, string token, int qty, decimal price) =>
        SubmitOrder(http, token, "Sell", qty, price);

    private static async Task<string> SubmitOrder(HttpClient http, string token, string side, int qty, decimal price)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = side,
                Type = "Limit",
                Quantity = qty,
                Price = price,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("clOrdId").GetString()!;
    }

    private static async Task InjectFill(HttpClient http, string adminToken, ulong clOrdId, long qty, decimal price)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(new
            {
                ClOrdId = clOrdId,
                Type = "Fill",
                LastQty = qty,
                LastPx = price,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    private static string ExtractSection(string csv, string header)
    {
        var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var i = Array.IndexOf(lines, header);
        Assert.True(i >= 0, $"section header not found: {header}");
        var sb = new StringBuilder();
        for (var j = i + 1; j < lines.Length; j++)
        {
            if (lines[j].Length == 0) break;
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append(lines[j]);
        }
        return sb.ToString();
    }

    private static List<string[]> ParseCsv(string block)
    {
        // RFC4180-lite: rows separated by CRLF, fields by comma,
        // optional double-quote wrap with embedded "" escape. Returns
        // the parsed rows so callers can assert on column shape.
        var rows = new List<string[]>();
        foreach (var line in block.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            if (line.Length == 0) continue;
            var fields = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') inQuotes = false;
                    else sb.Append(c);
                }
                else
                {
                    if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else if (c == '"' && sb.Length == 0) inQuotes = true;
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            rows.Add(fields.ToArray());
        }
        return rows;
    }
}
