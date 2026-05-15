using System.Text.Json;
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
/// <para>Pass-1 review fix: <see cref="MockEntryPointClient"/> does NOT
/// auto-emit a New ER on submit; every active scenario therefore
/// explicitly injects the venue <c>New</c> ER as the first ER in the
/// stream, before any subsequent fills/cancels, by calling
/// <see cref="ConformanceRunner.InjectErAsync"/> with type=<c>"New"</c>
/// once the order is visible in the WorkingOrderBook.</para>
///
/// <para>Pass-1 review fix: the golden's <c>scenario</c> block now
/// reflects values OBSERVED via <c>GET /orders/</c> (i.e. the platform's
/// persisted <see cref="OrderDto"/>) instead of the test's input
/// dictionary. An explicit pre-ER assertion compares observed-vs-expected
/// for type/TIF/price/stopPrice/qty/goodTillDate so a Q1.1 wiring drop
/// fails loudly with a clear diff before we ever touch the golden file.
/// </para>
///
/// <para>10 scenarios are active here; 2 are <c>[Skip]</c>'d pending
/// upstream <c>B3MatchingPlatform#321</c> (the phase scheduler that
/// orchestrates auction uncross transitions). The skipped bodies are
/// intentionally <c>Assert.Fail</c> stubs so that flipping <c>Skip</c>
/// off without rewriting the body to drive a real phase transition
/// surfaces immediately instead of silently passing on a synthetic
/// fill.</para>
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
    private const int CaptureTimeoutSeconds = 10;
    private const string Symbol = "PETR4";
    private const ulong SecurityId = 4321UL;

    // ------------------------------------------------------------------
    // Scenario 1 — Limit + Day. Submit, inject New, capture single ER.
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "Limit", expectedTif: "Day", expectedQty: 100, expectedPrice: 30m);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_01_LimitDay_BaselineNewEr.json");
    }

    // ------------------------------------------------------------------
    // Scenario 2 — Limit + IOC. Submit, inject New + PartialFill 40/100
    // + Canceled (IOC contract). Three ERs.
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "Limit", expectedTif: "IOC", expectedQty: 100, expectedPrice: 30m);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 3, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
                await runner.InjectErAsync(clOrdId, "PartialFill", lastQty: 40, lastPx: 30m);
                await runner.InjectErAsync(clOrdId, "Canceled");
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_02_LimitIoc_PartialFillThenCancel.json");
    }

    // ------------------------------------------------------------------
    // Scenario 3 — Limit + FOK. Fill-all branch: New, Fill (cum=qty).
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "Limit", expectedTif: "FOK", expectedQty: 100, expectedPrice: 30m);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
                await runner.InjectErAsync(clOrdId, "Fill", lastQty: 100, lastPx: 30m);
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_03_LimitFok_FillAll.json");
    }

    // ------------------------------------------------------------------
    // Scenario 4 — Limit + GTC. Inject New, capture, then verify the
    // order is still live shortly after.
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "Limit", expectedTif: "GTC", expectedQty: 100, expectedPrice: 30m);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_04_LimitGtc_RestsAndStaysLive.json");

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var order = await runner.GetOrderAsync(clOrdId);
        Assert.NotNull(order);
        var status = order!.Value.GetProperty("status").GetString();
        Assert.True(status is "Working" or "PartiallyFilled" or "PendingNew",
            $"GTC order expected to still be live, observed status={status}");
    }

    // ------------------------------------------------------------------
    // Scenario 5 — Limit + GTD. Inject New; the host's GTD scheduler
    // (Q1.3 / #255) emits a synthetic Expired ER when the deadline
    // elapses, then the regular cancel pipeline produces a Canceled ER.
    // Three ERs: New, Expired, Canceled.
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "Limit", expectedTif: "GTD", expectedQty: 100, expectedPrice: 30m,
            expectedGoodTillDateSet: true);

        // Wait long enough for the scheduler tick + cancel pipeline.
        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 3, TimeSpan.FromSeconds(15),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_05_LimitGtd_Expires.json");
    }

    // ------------------------------------------------------------------
    // Scenario 6 — Market + IOC. Sweep + cancel residual: three ERs
    // (New, PartialFill 60, Canceled).
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "Market", expectedTif: "IOC", expectedQty: 100, expectedPrice: null);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 3, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
                await runner.InjectErAsync(clOrdId, "PartialFill", lastQty: 60, lastPx: 30m);
                await runner.InjectErAsync(clOrdId, "Canceled");
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "Market", expectedTif: "FOK", expectedQty: 100, expectedPrice: null);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
                await runner.InjectErAsync(clOrdId, "Fill", lastQty: 100, lastPx: 30m);
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_07_MarketFok_FillAll.json");
    }

    // ------------------------------------------------------------------
    // Scenario 8 — MarketWithLeftover + Day. Sweep + rest: two ERs
    // (New, PartialFill 40).
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "MarketWithLeftover", expectedTif: "Day", expectedQty: 100, expectedPrice: 30m);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 2, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
                await runner.InjectErAsync(clOrdId, "PartialFill", lastQty: 40, lastPx: 30m);
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_08_MarketWithLeftoverDay_SweepThenRest.json");
    }

    // ------------------------------------------------------------------
    // Scenario 9 — StopLoss + Day. Single New ER captured.
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "StopLoss", expectedTif: "Day", expectedQty: 100,
            expectedPrice: null, expectedStopPrice: 28m);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_09_StopLossDay_Rests.json");
    }

    // ------------------------------------------------------------------
    // Scenario 10 — StopLimit + Day. Single New ER captured.
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
        var ctx = await CaptureObservedScenarioAsync(runner, clOrdId,
            expectedType: "StopLimit", expectedTif: "Day", expectedQty: 100,
            expectedPrice: 27m, expectedStopPrice: 28m);

        var ers = await runner.CaptureExecutionsWhileAsync(
            clOrdId, expectedCount: 1, TimeSpan.FromSeconds(CaptureTimeoutSeconds),
            async () =>
            {
                await runner.InjectErAsync(clOrdId, "New");
            });
        ConformanceRunner.AssertGoldenMatches(
            runner.Normalize(ers, ctx),
            "Q17_10_StopLimitDay_Rests.json");
    }

    // ------------------------------------------------------------------
    // Scenario 11 — Limit + GoodForAuction. SKIPPED pending upstream
    // B3MatchingPlatform#321. Body is intentionally Assert.Fail so that
    // un-skipping without rewriting to drive a real phase transition
    // surfaces immediately. Injecting a synthetic Fill would not exercise
    // auction phase transitions, reservations, or uncross emission and
    // would silently pass — defeating the purpose of the matrix.
    // ------------------------------------------------------------------
    [Fact(Skip = "blocked by upstream B3MatchingPlatform#321 (phase scheduler / auction uncross orchestration)")]
    public void Q17_LimitGoodForAuction_OpeningCallUncrossEmitsFill()
    {
        // TODO(B3MatchingPlatform#321): when the phase scheduler lands,
        // rewrite this body to:
        //   1. Submit the GoodForAuction order.
        //   2. Inject New ER (admission).
        //   3. Drive Continuous→OpeningAuction→Continuous via the phase
        //      scheduler admin hook (TBD endpoint shape).
        //   4. Capture the engine-emitted uncross Fill ER.
        //   5. Assert the captured sequence matches the golden.
        // Do NOT shortcut by injecting a synthetic Fill — that path
        // doesn't exercise reservations or uncross emission, which is
        // the whole point of the GoodForAuction TIF.
        Assert.Fail(
            "Body must be rewritten when B3MatchingPlatform#321 lands to drive a real phase transition " +
            "and observe a real uncross ER. Do NOT inject a synthetic Fill — see body comment.");
    }

    // ------------------------------------------------------------------
    // Scenario 12 — Limit + AtClose. SKIPPED pending upstream
    // B3MatchingPlatform#321. See Scenario 11 rationale.
    // ------------------------------------------------------------------
    [Fact(Skip = "blocked by upstream B3MatchingPlatform#321 (phase scheduler / closing auction orchestration)")]
    public void Q17_LimitAtClose_OnlyClosingUncrossExecutes()
    {
        // TODO(B3MatchingPlatform#321): when the phase scheduler lands,
        // rewrite this body to drive the closing-call uncross — see
        // the Q17_LimitGoodForAuction_* sibling for the analogous shape.
        // Do NOT shortcut by injecting a synthetic Fill.
        Assert.Fail(
            "Body must be rewritten when B3MatchingPlatform#321 lands to drive a real closing auction phase " +
            "transition and observe a real uncross ER. Do NOT inject a synthetic Fill — see body comment.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Pass-1 review fix: poll <c>GET /orders/</c> until the just-submitted
    /// order is visible, then assert the platform's persisted
    /// <see cref="OrderDto"/> matches the test's expected
    /// type/TIF/price/stopPrice/qty/goodTillDate values. Returns a
    /// scenario context dictionary populated from the OBSERVED DTO so
    /// the golden's <c>scenario</c> block reflects what
    /// REST→Domain→Persistence preserved (not what the test typed).
    /// A wiring drop fails this method with a clear field-by-field diff
    /// before the golden comparison ever runs.
    /// </summary>
    private static async Task<IDictionary<string, JsonNode?>> CaptureObservedScenarioAsync(
        ConformanceRunner runner, ulong clOrdId,
        string expectedType, string expectedTif, long expectedQty,
        decimal? expectedPrice = null, decimal? expectedStopPrice = null,
        bool expectedGoodTillDateSet = false)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        JsonElement? order = null;
        while (DateTime.UtcNow < deadline)
        {
            order = await runner.GetOrderAsync(clOrdId);
            if (order is not null) break;
            await Task.Delay(50);
        }
        if (order is null)
            throw new TimeoutException($"Order {clOrdId} did not become visible in /orders within 5s.");

        var observedType = order.Value.GetProperty("type").GetString();
        var observedTif = order.Value.GetProperty("timeInForce").GetString();
        var observedQty = order.Value.GetProperty("quantity").GetInt64();
        decimal? observedPrice = TryGetDecimal(order.Value, "price");
        decimal? observedStop = TryGetDecimal(order.Value, "stopPrice");
        var observedGtdSet = order.Value.TryGetProperty("goodTillDate", out var g)
            && g.ValueKind != JsonValueKind.Null;

        Assert.Equal(expectedType, observedType);
        Assert.Equal(expectedTif, observedTif);
        Assert.Equal(expectedQty, observedQty);
        Assert.Equal(expectedPrice, observedPrice);
        Assert.Equal(expectedStopPrice, observedStop);
        Assert.Equal(expectedGoodTillDateSet, observedGtdSet);

        var d = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["orderType"] = observedType,
            ["timeInForce"] = observedTif,
            ["orderQty"] = observedQty,
        };
        if (observedPrice is not null) d["price"] = observedPrice.Value;
        if (observedStop is not null) d["stopPrice"] = observedStop.Value;
        // GoodTillDate is volatile (DateTimeOffset.UtcNow.AddSeconds(2));
        // the assertion above already validates the field is present,
        // so the golden only records the boolean fact "was set" to keep
        // the snapshot stable across runs.
        if (observedGtdSet) d["goodTillDateSet"] = true;
        return d;
    }

    private static decimal? TryGetDecimal(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.GetDecimal()
            : null;
}
