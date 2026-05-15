using System.Text.Json.Nodes;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_Conformance_OrderTypeTif;

/// <summary>
/// Q1.7 (#259). Conformance golden snapshots for the (OrderType ×
/// TimeInForce) matrix. Each scenario submits a NewOrder with a specific
/// (OrderType, TIF) pair, drives a deterministic ER sequence via the
/// admin synthetic-injection seam (<c>POST /admin/simulator/er</c>), and
/// asserts the captured <c>executions.me</c> WS frames match a checked-in
/// golden after the platform-wide normalisation rules in
/// <see cref="ConformanceRunner.Normalize"/>.
///
/// <para>10 scenarios are active here; 2 are <c>[Skip]</c>'d pending
/// upstream <c>B3MatchingPlatform#321</c> (the phase scheduler that
/// orchestrates auction uncross transitions). The skipped scenarios are
/// fully written so they will run as soon as the upstream lands —
/// flipping the <c>Skip</c> attribute to a <c>ConformanceFact</c> will
/// suffice.</para>
///
/// <para>All scenarios are gated behind <c>RequiresErInjection=true</c>
/// + <c>RequiresAdmin=true</c>, mirroring the existing
/// <c>SimulatorErInjectionSpecTests</c> / <c>IcebergLifecycleSpecTests</c>:
/// the normal <c>dotnet test</c> run skips them at discovery, the
/// dedicated <c>docker-compose.conformance.yml</c> stack runs them.</para>
/// </summary>
[Trait("Category", "Conformance")]
public class OrderTypeTifMatrixSpecTests
{
    // The host's MockEntryPointClient auto-emits a New ER for every
    // admitted submit, so every scenario starts with at least one ER in
    // the WS stream before any synthetic injection.
    private const int CaptureTimeoutSeconds = 10;
    private const string Symbol = "PETR4";
    private const ulong SecurityId = 4321UL;

    // ------------------------------------------------------------------
    // Scenario 1 — Limit + Day (baseline). Just submit, capture the
    // platform's New ER. This is the existing-behaviour smoke test that
    // the rest of the matrix builds on; if it diverges from golden, the
    // submit→ER pipeline regressed.
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_LimitDay_BaselineNewEr()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "Day",
        });
        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Limit", "Day", qty: 100, price: 30m)),
            "Q17_01_LimitDay_BaselineNewEr.json");
    }

    // ------------------------------------------------------------------
    // Scenario 2 — Limit + IOC. Submit, partial-fill 40/100, then the
    // remainder is cancelled immediately (IOC contract). Three ERs:
    // New, PartialFill, Canceled.
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_LimitIoc_PartialFillThenCancelRemainder()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "IOC",
        });
        await WaitForFirstErAsync(runner, clOrdId);
        await runner.InjectErAsync(clOrdId, "PartialFill", lastQty: 40, lastPx: 30m);
        await runner.InjectErAsync(clOrdId, "Canceled");

        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 3, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Limit", "IOC", qty: 100, price: 30m)),
            "Q17_02_LimitIoc_PartialFillThenCancel.json");
    }

    // ------------------------------------------------------------------
    // Scenario 3 — Limit + FOK. Fill-all-or-cancel-all. We exercise the
    // fill-all branch (the cancel-all branch would need market-state
    // pre-condition that the synthetic seam doesn't model). Two ERs:
    // New, Fill (cum=qty).
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_LimitFok_FillAll()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "FOK",
        });
        await WaitForFirstErAsync(runner, clOrdId);
        await runner.InjectErAsync(clOrdId, "Fill", lastQty: 100, lastPx: 30m);

        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Limit", "FOK", qty: 100, price: 30m)),
            "Q17_03_LimitFok_FillAll.json");
    }

    // ------------------------------------------------------------------
    // Scenario 4 — Limit + GTC. Submit, capture the New ER, verify the
    // order is still live shortly after (representing "survives the
    // session window" within the test's wall-clock budget — the host
    // does not expose an admin /admin/daily-reset hook in the current
    // surface, see PR notes for follow-up).
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_LimitGtc_RestsAndStaysLive()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "GTC",
        });
        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Limit", "GTC", qty: 100, price: 30m)),
            "Q17_04_LimitGtc_RestsAndStaysLive.json");

        // Live-after-pause check: the GTC order must not get auto-
        // cancelled within the test's natural wall-clock budget. Polls
        // /orders/ to confirm the working order is still present and
        // not in a terminal state.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var order = await runner.GetOrderAsync(clOrdId);
        Assert.NotNull(order);
        var status = order!.Value.GetProperty("status").GetString();
        Assert.True(status is "Working" or "PartiallyFilled" or "PendingNew",
            $"GTC order expected to still be live, observed status={status}");
    }

    // ------------------------------------------------------------------
    // Scenario 5 — Limit + GTD. Submit with a near-future expiry; the
    // host's GTD scheduler (Q1.3 / #255) emits a synthetic Expired ER
    // when the deadline elapses, then the regular cancel pipeline
    // produces a Canceled ER. Three ERs: New, Expired, Canceled.
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_LimitGtd_ExpiresAtDeadline()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var goodTill = DateTimeOffset.UtcNow.AddSeconds(2);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "GTD",
            GoodTillDate = goodTill,
        });
        // Wait long enough for the scheduler tick + cancel pipeline.
        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 3, TimeSpan.FromSeconds(15));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Limit", "GTD", qty: 100, price: 30m)),
            "Q17_05_LimitGtd_Expires.json");
    }

    // ------------------------------------------------------------------
    // Scenario 6 — Market + IOC. Sweeps marketable book, residual
    // cancelled. Modeled here as Fill 60 + Canceled 40 to exercise the
    // partial-sweep+cancel-residual sequence. Three ERs.
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_MarketIoc_SweepThenCancelResidual()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Market",
            Quantity = 100,
            Price = (decimal?)null,
            TimeInForce = "IOC",
        });
        await WaitForFirstErAsync(runner, clOrdId);
        await runner.InjectErAsync(clOrdId, "PartialFill", lastQty: 60, lastPx: 30m);
        await runner.InjectErAsync(clOrdId, "Canceled");

        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 3, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Market", "IOC", qty: 100, price: null)),
            "Q17_06_MarketIoc_SweepThenCancel.json");
    }

    // ------------------------------------------------------------------
    // Scenario 7 — Market + FOK. Fill-all branch: New, Fill.
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_MarketFok_FillAll()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Market",
            Quantity = 100,
            Price = (decimal?)null,
            TimeInForce = "FOK",
        });
        await WaitForFirstErAsync(runner, clOrdId);
        await runner.InjectErAsync(clOrdId, "Fill", lastQty: 100, lastPx: 30m);

        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Market", "FOK", qty: 100, price: null)),
            "Q17_07_MarketFok_FillAll.json");
    }

    // ------------------------------------------------------------------
    // Scenario 8 — MarketWithLeftover + Day. Marketable up to Price;
    // residual rests on book as a Day Limit. The conformance contract
    // is the ER stream: New, PartialFill (sweep portion). The leftover
    // remains a working order (no terminal cancel ER).
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_MarketWithLeftoverDay_SweepThenRest()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "MarketWithLeftover",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "Day",
        });
        await WaitForFirstErAsync(runner, clOrdId);
        await runner.InjectErAsync(clOrdId, "PartialFill", lastQty: 40, lastPx: 30m);

        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("MarketWithLeftover", "Day", qty: 100, price: 30m)),
            "Q17_08_MarketWithLeftoverDay_SweepThenRest.json");
    }

    // ------------------------------------------------------------------
    // Scenario 9 — StopLoss + Day. Stop order's New ER is the contract
    // surface; the trigger-into-Market is the matching engine's
    // responsibility (and would be modeled as a separate child clOrdId
    // upstream). Single New ER captured.
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_StopLossDay_RestsAtStop()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Sell",
            Type = "StopLoss",
            Quantity = 100,
            Price = (decimal?)null,
            StopPrice = 28m,
            TimeInForce = "Day",
        });
        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("StopLoss", "Day", qty: 100, price: null, stopPrice: 28m)),
            "Q17_09_StopLossDay_Rests.json");
    }

    // ------------------------------------------------------------------
    // Scenario 10 — StopLimit + Day. Same as 9, with a Price too.
    // ------------------------------------------------------------------
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Q17_StopLimitDay_RestsAtStop()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Sell",
            Type = "StopLimit",
            Quantity = 100,
            Price = 27m,
            StopPrice = 28m,
            TimeInForce = "Day",
        });
        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("StopLimit", "Day", qty: 100, price: 27m, stopPrice: 28m)),
            "Q17_10_StopLimitDay_Rests.json");
    }

    // ------------------------------------------------------------------
    // Scenario 11 — Limit + GoodForAuction. Requires the upstream
    // matching-platform phase scheduler (B3MatchingPlatform#321) to
    // orchestrate phase transitions and emit the uncross fill. Body is
    // ready to run; flip the Skip to ConformanceFact when upstream
    // lands.
    // ------------------------------------------------------------------
    [Fact(Skip = "blocked by upstream B3MatchingPlatform#321 (phase scheduler / auction uncross orchestration)")]
    public async Task Q17_LimitGoodForAuction_OpeningCallUncrossEmitsFill()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "GoodForAuction",
        });
        await WaitForFirstErAsync(runner, clOrdId);
        // When upstream lands, the phase scheduler will flip
        // Continuous→OpeningAuction→Continuous and the engine will
        // emit a Fill at the call price. Until then the synthetic
        // seam can't model the phase transition; the scenario is
        // intentionally code-complete so the only diff to enable is
        // the attribute.
        await runner.InjectErAsync(clOrdId, "Fill", lastQty: 100, lastPx: 30m);

        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Limit", "GoodForAuction", qty: 100, price: 30m)),
            "Q17_11_LimitGoodForAuction_OpeningCallFill.json");
    }

    // ------------------------------------------------------------------
    // Scenario 12 — Limit + AtClose. Same upstream dependency: only
    // the closing call uncross may execute the order. Body is ready
    // to run.
    // ------------------------------------------------------------------
    [Fact(Skip = "blocked by upstream B3MatchingPlatform#321 (phase scheduler / closing auction orchestration)")]
    public async Task Q17_LimitAtClose_OnlyClosingUncrossExecutes()
    {
        await using var runner = await ConformanceRunner.CreateAsync(PlatformEndpoint.TryResolve()!);
        var clOrdId = await runner.SubmitOrderAsync(new
        {
            Symbol,
            SecurityId,
            Side = "Sell",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "AtClose",
        });
        await WaitForFirstErAsync(runner, clOrdId);
        await runner.InjectErAsync(clOrdId, "Fill", lastQty: 100, lastPx: 30m);

        var ers = await runner.CaptureExecutionsAsync(clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds));
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ScenarioCtx("Limit", "AtClose", qty: 100, price: 30m)),
            "Q17_12_LimitAtClose_ClosingUncrossFill.json");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Wait for the platform to emit the implicit New ER before we start
    /// injecting synthetic follow-ups. Without this gate the simulator
    /// endpoint can race and reject the inject because the WorkingOrder
    /// hasn't materialised yet (the inject endpoint reads from
    /// WorkingOrderBook).
    /// </summary>
    private static async Task WaitForFirstErAsync(ConformanceRunner runner, ulong clOrdId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var o = await runner.GetOrderAsync(clOrdId);
            if (o is not null) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Order {clOrdId} did not become visible in /orders within 5s.");
    }

    private static IDictionary<string, JsonNode?> ScenarioCtx(
        string orderType, string tif, long qty, decimal? price,
        decimal? stopPrice = null)
    {
        var d = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["orderType"] = orderType,
            ["timeInForce"] = tif,
            ["orderQty"] = qty,
        };
        if (price is not null) d["price"] = price.Value;
        if (stopPrice is not null) d["stopPrice"] = stopPrice.Value;
        return d;
    }
}
