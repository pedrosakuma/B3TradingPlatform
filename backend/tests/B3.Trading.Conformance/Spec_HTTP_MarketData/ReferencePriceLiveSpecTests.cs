using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_MarketData;

/// <summary>
/// Spec — MarketData. A trade that prints on the matching engine must
/// reach the trading-host through the live UMDF + WS leg and displace
/// the static fallback in <c>MarketDataReferencePrice</c>. This spec
/// proves the end-to-end loop: matching → marketdata UMDF → marketdata
/// WS → trading-host cache → <c>IReferencePrice</c> diagnostics.
///
/// <para>
/// Gate: <see cref="ConformanceFactAttribute.RequiresSandboxMatching"/>.
/// The scenario submits a real crossed-pair (buy + sell at the same
/// price/qty) and expects matching to print a trade — destructive in
/// any non-sandbox environment, so it stays skipped unless the
/// operator explicitly opts in via
/// <c>B3T_REAL_STACK_CONFORMANCE=true</c>. The docker-compose
/// real-stack overlay sets the flag; nothing else does.
/// </para>
///
/// <para>
/// Design notes (rubber-duck'd):
/// <list type="bullet">
/// <item>Pre-cross baseline is captured but NOT asserted to be
///       <c>Fallback</c> — <c>InfoSnapshot.TradingReferencePrice</c>
///       could pre-populate the live cache before this test runs. The
///       deterministic invariant is "after the cross, live.price equals
///       the cross price AND live.updatedUtc advanced past the moment
///       we started submitting", regardless of pre-warmed state.</item>
/// <item>Cross price is chosen distinct from the configured static
///       fallback (30.00) so a coincidental fallback->live transition
///       at the wrong price would still fail the assertion.</item>
/// <item>Quantity 100 / price 31.00 respects matching's lot/tick
///       constraints (lot=100, tick=0.01 in instruments-eqt.json) and
///       sits well inside the default ±10% collar around the static
///       fallback (range [27.00, 33.00]).</item>
/// <item>30s timeout covers the multi-hop async path (FIXP submit →
///       matching execute → UMDF UDP → marketdata WS → trading-host
///       cache update). 250ms poll keeps the failure window tight.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "Conformance")]
public class ReferencePriceLiveSpecTests
{
    private const string Symbol = "ITUB4";
    private const decimal CrossPrice = 31.00m;
    private const long CrossQuantity = 100;
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true)]
    public async Task CrossedTrade_DisplacesFallback_ReachesLiveCacheWithCrossPrice()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);

        // Baseline — capture (and log) but do not assert source. The
        // live cache may already be primed by an InfoSnapshot from the
        // matching emitter's startup cycle, in which case effectiveSource
        // is already Live before any trade has actually printed. The
        // deterministic step is the post-cross assertion below.
        var baseline = await GetReferencePriceAsync(http, adminAuth, Symbol);
        Assert.Equal(Symbol, baseline.Symbol);
        // Static fallback must be configured for this symbol — otherwise
        // we couldn't prove "live displaced fallback", we'd only prove
        // "live filled a hole". The real overlay seeds it explicitly.
        Assert.NotNull(baseline.FallbackPrice);

        // Mark the moment we start submitting; the live cache update
        // for the trade we're about to print MUST arrive after this.
        var submitStartUtc = DateTimeOffset.UtcNow;

        // Cross pair from the same authenticated end-client. The
        // matching bridge is configured with selfTradePrevention=none
        // (docker/real/exchange-simulator.bridge.json), so opposite
        // sides at the same price/qty will execute against each other.
        await SubmitOrderAndAssertAcceptedAsync(http, userAuth, side: "Buy");
        await SubmitOrderAndAssertAcceptedAsync(http, userAuth, side: "Sell");

        // Poll the diagnostics endpoint until the live cache reports the
        // cross price OR we exhaust the budget. On timeout, dump every
        // observation we can to make the failure debuggable.
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        ReferencePriceDiagnostic? lastSeen = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await GetReferencePriceAsync(http, adminAuth, Symbol);
            lastSeen = current;
            if (current.Live is { } live &&
                live.Price == CrossPrice &&
                live.UpdatedUtc > submitStartUtc)
            {
                // Bonus invariant: with the live cache hit, the effective
                // resolution must reflect Live (not Fallback / Missing).
                Assert.Equal("Live", current.EffectiveSource);
                Assert.Equal(CrossPrice, current.EffectivePrice);
                return;
            }
            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {PollTimeout.TotalSeconds:F0}s waiting for {Symbol} live ref-price=={CrossPrice} updated after {submitStartUtc:o}. " +
            $"Baseline: {Format(baseline)}. Last seen: {Format(lastSeen)}.");
    }

    private static async Task SubmitOrderAndAssertAcceptedAsync(
        HttpClient http, AuthenticationHeaderValue auth, string side)
    {
        // SymbolDirectory in the real overlay maps ITUB4 → 900000000003
        // (matching the instruments file the matching-platform loads),
        // so we can omit securityId and let the host resolve.
        using var submit = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new
            {
                symbol = Symbol,
                side,
                type = "Limit",
                quantity = CrossQuantity,
                price = CrossPrice,
            }),
        };

        var resp = await http.SendAsync(submit);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Accepted,
            $"{side} POST /orders expected 202 Accepted, got {(int)resp.StatusCode}: {body}");

        // 202 Accepted is also returned for risk-rejected orders (with
        // status="Rejected" in the body — see RiskRejectionShapeSpec).
        // For this scenario, we need actual acceptance through to the
        // gateway; assert the status field is absent or != "Rejected".
        if (!string.IsNullOrWhiteSpace(body))
        {
            var json = JsonDocument.Parse(body).RootElement;
            if (json.TryGetProperty("status", out var statusProp))
            {
                var status = statusProp.GetString();
                Assert.True(!string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase),
                    $"{side} POST /orders was risk-rejected before reaching matching: {body}");
            }
        }
    }

    private static async Task<ReferencePriceDiagnostic> GetReferencePriceAsync(
        HttpClient http, AuthenticationHeaderValue auth, string symbol)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/admin/marketdata/reference-prices?symbols={Uri.EscapeDataString(symbol)}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entry = json.GetProperty("symbols")[0];

        LiveBlock? live = null;
        if (entry.TryGetProperty("live", out var liveProp) && liveProp.ValueKind == JsonValueKind.Object)
        {
            live = new LiveBlock(
                liveProp.GetProperty("price").GetDecimal(),
                liveProp.GetProperty("updatedUtc").GetDateTimeOffset());
        }

        return new ReferencePriceDiagnostic(
            Symbol: entry.GetProperty("symbol").GetString()!,
            EffectivePrice: entry.GetProperty("effectivePrice").ValueKind == JsonValueKind.Null
                ? null : entry.GetProperty("effectivePrice").GetDecimal(),
            EffectiveSource: entry.GetProperty("effectiveSource").GetString()!,
            Live: live,
            FallbackPrice: entry.GetProperty("fallbackPrice").ValueKind == JsonValueKind.Null
                ? null : entry.GetProperty("fallbackPrice").GetDecimal());
    }

    private static string Format(ReferencePriceDiagnostic? d) => d is null
        ? "<none>"
        : $"{{ source={d.EffectiveSource}, effective={d.EffectivePrice?.ToString() ?? "null"}, live={(d.Live is { } l ? $"{l.Price}@{l.UpdatedUtc:o}" : "null")}, fallback={d.FallbackPrice?.ToString() ?? "null"} }}";

    private sealed record ReferencePriceDiagnostic(
        string Symbol,
        decimal? EffectivePrice,
        string EffectiveSource,
        LiveBlock? Live,
        decimal? FallbackPrice);

    private sealed record LiveBlock(decimal Price, DateTimeOffset UpdatedUtc);
}
