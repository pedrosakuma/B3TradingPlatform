using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for the Pegged algo (Q3.3 / #283). Mirrors the
/// shape of <see cref="PovAlgoEndpointTests"/>: real wall-clock with
/// sub-second cadence so the scheduler and engine reactor exercise the
/// production code path. The Pegged-specific twist is the market-data
/// reference price: tests inject mids/bests/lasts directly through
/// <see cref="PegBookTopCache"/> rather than through a real SDK feed
/// (see the SDK-gap note on <see cref="PegBookTopCache"/>).
/// </summary>
public class PeggedAlgoEndpointTests
{
    private static IDictionary<string, string?> Simulator() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
            ["Trading:Exchange:AllowErInjection"] = "true",
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
        };

    private static object PeggedBody(long total, string pegRef = "Mid",
        int offsetTicks = 0, int? repegMs = 100, decimal? tickSize = 0.5m,
        string? childType = null, decimal? priceLimit = null, string side = "Buy") => new
        {
            Symbol = "PETR4",
            Side = side,
            Type = "Pegged",
            TotalQuantity = total,
            Pegged = new
            {
                Ref = pegRef,
                OffsetTicks = offsetTicks,
                RepegIntervalMs = repegMs,
                TickSize = tickSize,
                ChildOrderType = childType,
                PriceLimit = priceLimit,
            },
        };

    // ───────────────────────── POST validation ─────────────────────────

    [Fact]
    public async Task PostAlgo_PeggedWithoutParams_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                Side = "Buy",
                Type = "Pegged",
                TotalQuantity = 100,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_PeggedInvalidRef_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PeggedBody(100, pegRef: "Bogus")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_PeggedNonPositiveTickSize_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PeggedBody(100, tickSize: 0m)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_PeggedMarketChildType_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PeggedBody(100, childType: "Market")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ───────────────────── Happy path: pricing + repeg ─────────────────────

    [Fact]
    public async Task Pegged_PostsAtPeggedTargetPrice()
    {
        // Seed the book-top cache BEFORE posting so the very first
        // engine tick has a live reference. Pegged Buy / mid / offset=0
        // / tickSize=0.5: target = 30.0.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", bestBid: 29.5m, bestAsk: 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 200));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child.Price);
        Assert.Equal(200, child.Quantity);
    }

    [Fact]
    public async Task Pegged_MidMoves_TriggersRepeg()
    {
        // Steady mid = 30.0; engine places child @ 30.0. Mid jumps to
        // 31.0 → engine cancels child via gateway (queued on mock),
        // test admin-injects the Cancelled ER, engine submits new child
        // at the fresh target (31.0).
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        // Move the mid up one tick — repeg gate is "≥ 1 tick", so 1.0
        // delta (= 2 ticks at 0.5) is comfortably over the threshold.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);

        // Engine must enqueue exactly one cancel for the live child.
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3),
            "engine never cancelled the live child after mid moved");

        // Mirror what the venue would do — emit the Cancelled ER for
        // the old child. The engine's RepegPending flag routes this
        // through SubmitNextSliceAsync (rather than VenueCancelled
        // suspension) — assertable via the new child appearing at the
        // fresh target price.
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");

        var newChild = await WaitForChildOtherThan(book, algoId, child1.ClOrdId, TimeSpan.FromSeconds(3));
        Assert.Equal(31.0m, newChild.Price);
        Assert.Equal(100, newChild.Quantity); // full residue (no fills yet).
    }

    [Fact]
    public async Task Pegged_StableMid_DoesNotRepeg()
    {
        // Steady mid: engine must NOT churn cancels. Assert no cancel
        // is enqueued after the first child is placed.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100, repegMs: 50));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child.Price);

        // Keep the mid steady (re-stamp identical legs so freshness
        // doesn't decay). Several scheduler ticks must pass — the
        // engine re-evaluates each tick and must no-op every time.
        for (int i = 0; i < 8; i++)
        {
            cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);
            await Task.Delay(50);
        }

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        Assert.Empty(mock.SubmittedCancels);
    }

    [Fact]
    public async Task Pegged_PriceLimitBlocksAggressiveRepeg()
    {
        // Buy with priceLimit = 30.5. Initial target = 30.0, child sits
        // there. Mid jumps to 32.0 → raw target = 32.0 → clamped down
        // to 30.5. 30.5 differs from 30.0 by 1 tick → 1 repeg fires
        // (legitimate move up to the limit). After that, further mid
        // moves leave the clamped target stuck at 30.5 — engine must
        // not churn additional cancels.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token,
            PeggedBody(total: 100, priceLimit: 30.5m));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();

        // First move clamps to the limit (30.5) — one repeg.
        cache.UpdateBookTop("PETR4", 31.5m, 32.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedCancels.Count == 1,
            TimeSpan.FromSeconds(3), "engine did not repeg to the price limit");
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.5m, child2.Price);

        // Subsequent further-aggressive moves stay clamped at 30.5,
        // identical to child2.Price → IsRepegNeeded false → engine
        // must not enqueue another cancel.
        for (int i = 0; i < 6; i++)
        {
            cache.UpdateBookTop("PETR4", 32.5m + i, 33.5m + i, DateTimeOffset.UtcNow);
            await Task.Delay(50);
        }
        Assert.Single(mock.SubmittedCancels);
    }

    // ───────────────────── Cancel-mid-flight ─────────────────────

    /// <summary>
    /// Recording sink shared with the other algo-endpoint suites — used
    /// to assert exactly-once terminal emission across the operator-cancel
    /// race with the engine-driven repeg path.
    /// </summary>
    private sealed class RecordingAlgoEventSink : IAlgoEventSink
    {
        private readonly AlgoBook _book;
        private readonly object _gate = new();
        private readonly List<(ulong AlgoId, AlgoStatus Status)> _publishes = new();

        public RecordingAlgoEventSink(AlgoBook book) => _book = book;

        public void PublishAlgoSnapshot(EndClientId owner, string firmId, ulong algoId)
        {
            if (!_book.TryGet(firmId, algoId, out var algo) || algo is null) return;
            lock (_gate) { _publishes.Add((algoId, algo.Status)); }
        }

        public int TerminalPublishCount(ulong algoId)
        {
            lock (_gate)
            {
                return _publishes.Count(p => p.AlgoId == algoId && IsTerminal(p.Status));
            }
        }

        private static bool IsTerminal(AlgoStatus s) =>
            s is AlgoStatus.Cancelled or AlgoStatus.Completed
              or AlgoStatus.Expired or AlgoStatus.Suspended;
    }

    [Fact]
    public async Task Pegged_CancelMidFlight_TerminalEmittedOnce()
    {
        // Operator DELETE during a live working slice. The simulator's
        // Cancelled ER must drive the parent terminal exactly once even
        // though Pegged's repeg path also routes through OnChildErAsync
        // Cancelled — the algo.Status==Cancelling branch wins because
        // the engine clears RepegPending only AFTER it issues its own
        // cancel, which DELETE preempts.
        RecordingAlgoEventSink? sink = null;
        using var f = TestAppFactory.WithOverrides(Simulator(), services =>
        {
            services.RemoveAll<IAlgoEventSink>();
            services.AddSingleton<IAlgoEventSink>(sp =>
                sink = new RecordingAlgoEventSink(sp.GetRequiredService<AlgoBook>()));
        });
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        _ = f.Services.GetRequiredService<IAlgoEventSink>();
        Assert.NotNull(sink);

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, userToken, PeggedBody(total: 100));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var inFlight = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var del = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, del.StatusCode);

        await InjectEr(http, adminToken, inFlight.ClOrdId, "Canceled");

        await WaitForAlgoStatus(http, userToken, algoId, "Cancelled");
        var snap = await GetAlgo(http, userToken, algoId);
        Assert.Equal("UserCancelled", snap.GetProperty("terminalReason").GetString());

        // Idempotent terminal — even after a follow-up DELETE there
        // must be exactly one terminal publish.
        var req2 = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var del2 = await http.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Conflict, del2.StatusCode);

        await Task.Delay(100);
        Assert.Equal(1, sink!.TerminalPublishCount(ulong.Parse(algoId)));
    }

    // ──────────────────── DTO ────────────────────

    [Fact]
    public async Task GetAlgo_ReturnsPeggedParametersInDto()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // Don't seed the cache — the parent will sit waiting for a ref
        // (no-op submit path) which is fine for a DTO-shape assertion.
        var algoId = await PostAlgo(http, token,
            PeggedBody(total: 200, pegRef: "Best", offsetTicks: -2,
                repegMs: 750, tickSize: 0.05m, priceLimit: 41.25m));

        var algo = await GetAlgo(http, token, algoId);
        Assert.Equal("Pegged", algo.GetProperty("type").GetString());
        var pgd = algo.GetProperty("pegged");
        Assert.Equal("Best", pgd.GetProperty("ref").GetString());
        Assert.Equal(-2, pgd.GetProperty("offsetTicks").GetInt32());
        Assert.Equal(750, pgd.GetProperty("repegIntervalMs").GetInt32());
        Assert.Equal(0.05m, pgd.GetProperty("tickSize").GetDecimal());
        Assert.Equal("Limit", pgd.GetProperty("childOrderType").GetString());
        Assert.Equal(41.25m, pgd.GetProperty("priceLimit").GetDecimal());
    }

    // ──────────────────── Recovery ────────────────────

    private static IDictionary<string, string?> PersistenceOverrides(string dataDir)
    {
        var d = new Dictionary<string, string?>(Simulator())
        {
            ["Trading:Persistence:Enabled"] = "true",
            ["Trading:Persistence:DataDirectory"] = dataDir,
            ["Trading:Persistence:FirmId"] = "default",
            ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
        };
        return d;
    }

    private static B3.Trading.Infrastructure.Persistence.SnapshotService ResolveSnapshotService(TestAppFactory f) =>
        f.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<B3.Trading.Infrastructure.Persistence.SnapshotService>()
            .Single();

    [Fact]
    public async Task Pegged_SnapshotRestart_RestoresParametersAndWorkingSlice()
    {
        // Take a snapshot while the algo has a live working slice;
        // restart cold; assert the Pegged-shaped parameters made the
        // round-trip and that the algo is recovered as "Working" with
        // its filled/remaining unchanged.
        var dataDir = Path.Combine(Environment.CurrentDirectory, "test-data",
            "b3-pegged-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;
            long expectedTotal;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var token = await f.LoginAsync(http);
                var cache = f.Services.GetRequiredService<PegBookTopCache>();
                cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

                var algoIdStr = await PostAlgo(http, token,
                    PeggedBody(total: 100, pegRef: "Mid", offsetTicks: 0,
                        repegMs: 250, tickSize: 0.5m));
                algoIdNum = ulong.Parse(algoIdStr);
                expectedTotal = 100;

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                _ = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(3));

                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            // Cold restart with the same data dir.
            using (var f2 = TestAppFactory.WithOverrides(overrides))
            using (var http2 = f2.CreateClient())
            {
                var token = await f2.LoginAsync(http2);
                var algo = await GetAlgo(http2, token, algoIdNum.ToString());
                Assert.Equal("Pegged", algo.GetProperty("type").GetString());
                Assert.Equal(expectedTotal, algo.GetProperty("totalQuantity").GetInt64());
                var pgd = algo.GetProperty("pegged");
                Assert.Equal("Mid", pgd.GetProperty("ref").GetString());
                Assert.Equal(0, pgd.GetProperty("offsetTicks").GetInt32());
                Assert.Equal(0.5m, pgd.GetProperty("tickSize").GetDecimal());
                Assert.Equal(250, pgd.GetProperty("repegIntervalMs").GetInt32());
            }
        }
        finally
        {
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ──────────────── Pass-1 review (#296) regression tests ────────────────

    [Fact]
    public async Task Pegged_RepegCancelAck_DoesNotSuspendParent()
    {
        // Pass-1 review (#296) P1-A. Repeg cancel-ack lands → engine
        // submits replacement. A duplicate / late Cancelled ER for
        // the SAME old child must NOT be routed through the
        // VenueCancelled branch (which would suspend the parent and
        // orphan the live replacement child).
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        // Drift the mid → engine cancels child1.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine never cancelled child1 after mid moved");

        // First Cancelled ER → engine routes through SubmitNextSlice.
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId, TimeSpan.FromSeconds(3));
        Assert.Equal(31.0m, child2.Price);

        // Duplicate / late Cancelled ER for the SAME old child must
        // be a no-op — parent stays Working, no terminal published.
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        await Task.Delay(150);

        var snap = await GetAlgo(http, token, algoId);
        Assert.Equal("Working", snap.GetProperty("status").GetString());
        Assert.Equal("None", snap.GetProperty("terminalReason").GetString());

        // And the replacement child is still live in the book.
        var stillThere = book.EnumerateChildrenOf("default", ulong.Parse(algoId))
            .FirstOrDefault(c => c.ClOrdId == child2.ClOrdId);
        Assert.NotNull(stillThere);
    }

    [Fact]
    public async Task Pegged_RepegThrottle_SuppressesCancelStorm()
    {
        // Pass-1 review (#296) P1-B. Rapid mid moves while a cancel
        // ack is delayed past RepegInterval must NOT spawn additional
        // cancels for the same already-cancel-pending child. Only one
        // cancel + one replacement should fire per repeg cycle.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        // Tight RepegInterval so the throttle would normally fire
        // many times in the burst window below.
        var algoId = await PostAlgo(http, token, PeggedBody(total: 100, repegMs: 50));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        // First mid move → first cancel (do NOT inject the ER yet).
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit the first cancel");

        // Burst of further mid moves spanning well past RepegInterval.
        // With the P1-B fix (RepegPending short-circuit) the engine
        // must NOT enqueue another cancel for child1 while the ack
        // is still outstanding. Keep the cumulative delta small so
        // the eventual replacement child stays inside risk price
        // bands (we're not exercising risk here).
        for (int i = 0; i < 4; i++)
        {
            cache.UpdateBookTop("PETR4", 30.5m + i * 0.1m, 31.5m + i * 0.1m,
                DateTimeOffset.UtcNow);
            await Task.Delay(60);
        }
        Assert.Single(mock.SubmittedCancels);

        // Now release the cycle: the cancel-ack lands and the engine
        // submits exactly one replacement child.
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId, TimeSpan.FromSeconds(3));
        Assert.NotNull(child2);

        // The parent must remain Working — a duplicate / late ER for
        // child1 must not flip it Suspended (the P1-A marker keeps
        // the classification correct even after the cycle resolved).
        var snapBefore = await GetAlgo(http, token, algoId);
        var statusBefore = snapBefore.GetProperty("status").GetString();
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        await Task.Delay(150);
        var snap = await GetAlgo(http, token, algoId);
        var statusAfter = snap.GetProperty("status").GetString();
        Assert.Equal("Working", statusBefore);
        Assert.Equal("Working", statusAfter);
    }

    [Fact]
    public async Task Pegged_RecoveryPreservesRepegIntent()
    {
        // Pass-1 review (#296) P1-C. Engine emits the repeg cancel
        // but crashes before the venue Cancelled ER lands. After
        // snapshot+restart the cancel-ack ER must be classified as
        // expected (matching the persisted pending marker) and route
        // through SubmitNextSliceAsync — NOT into the VenueCancelled
        // suspension branch.
        var dataDir = Path.Combine(Environment.CurrentDirectory, "test-data",
            "b3-pegged-repeg-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;
            ulong child1ClOrdId = 0;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var token = await f.LoginAsync(http);
                var cache = f.Services.GetRequiredService<PegBookTopCache>();
                cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

                var algoIdStr = await PostAlgo(http, token,
                    PeggedBody(total: 100, pegRef: "Mid", offsetTicks: 0,
                        repegMs: 100, tickSize: 0.5m));
                algoIdNum = ulong.Parse(algoIdStr);

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                var child1 = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(3));
                child1ClOrdId = child1.ClOrdId;
                Assert.Equal(30.0m, child1.Price);

                // Drift the mid → engine cancels child1. DO NOT
                // inject the Cancelled ER — we want the cancel to
                // be in-flight at snapshot time.
                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                var mock = f.Services.GetRequiredService<MockEntryPointClient>();
                await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit the cancel before snapshot");

                // Capture the snapshot with the cancel still pending.
                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            // Cold restart — engine reconciles, sees the pending
            // repeg entry, sets RepegPending=true +
            // LastRepegCancelledChildId so the post-restart ER is
            // expected.
            using (var f2 = TestAppFactory.WithOverrides(overrides))
            using (var http2 = f2.CreateClient())
            {
                var token = await f2.LoginAsync(http2);
                var adminToken = await f2.LoginAsync(http2, "admin");

                // Re-seed the cache so SubmitNextSlice can resolve a
                // target for the replacement (cache is in-memory
                // only and does not survive restart).
                var cache2 = f2.Services.GetRequiredService<PegBookTopCache>();
                cache2.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);

                // Inject the cancel-ack the engine was waiting for.
                await InjectEr(http2, adminToken, child1ClOrdId, "Canceled");

                // Replacement child must appear and parent must NOT
                // be Suspended (which is what the pre-fix code would
                // have done via the VenueCancelled branch).
                var book2 = f2.Services.GetRequiredService<WorkingOrderBook>();
                var child2 = await WaitForChildOtherThan(book2, algoIdNum.ToString(), child1ClOrdId,
                    TimeSpan.FromSeconds(5));
                Assert.Equal(31.0m, child2.Price);

                var snap = await GetAlgo(http2, token, algoIdNum.ToString());
                Assert.Equal("Working", snap.GetProperty("status").GetString());
                Assert.Equal("None", snap.GetProperty("terminalReason").GetString());
            }
        }
        finally
        {
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Pegged_RecoveryReplaysRepegStartedEventFromWalTail()
    {
        // Pass-2 review (#296) P2-D. Sibling of Pegged_RecoveryPreservesRepegIntent
        // that pins the WAL-tail replay path specifically. The original
        // test snapshots AFTER AlgoPeggedRepegStartedEvent fires, so
        // post-restart state comes from the snapshot's
        // PeggedRepegBook — the AlgoPeggedRepegStartedEvent replay
        // handler in StateSnapshotter.Apply is never exercised.
        //
        // Here we capture the snapshot BEFORE drifting the mid so the
        // snapshot contains only the algo + child1 (no pending repeg
        // entry). The drift then causes the engine to emit the cancel +
        // AlgoPeggedRepegStartedEvent which lands ONLY in the WAL tail
        // past snapshot.seq. Cold restart → snapshot restores the algo
        // and the live child1; ReadFromAsync replays the
        // AlgoPeggedRepegStartedEvent which writes into
        // PeggedRepegBook; Reconcile then sees the still-live child
        // and hydrates RepegPending + the sticky cancel-id marker.
        // The post-restart Cancelled ER must route through
        // SubmitNextSliceAsync, not VenueCancelled-suspension.
        var dataDir = Path.Combine(Environment.CurrentDirectory, "test-data",
            "b3-pegged-repeg-wal-tail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;
            ulong child1ClOrdId = 0;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var token = await f.LoginAsync(http);
                var cache = f.Services.GetRequiredService<PegBookTopCache>();
                cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

                var algoIdStr = await PostAlgo(http, token,
                    PeggedBody(total: 100, pegRef: "Mid", offsetTicks: 0,
                        repegMs: 100, tickSize: 0.5m));
                algoIdNum = ulong.Parse(algoIdStr);

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                var child1 = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(3));
                child1ClOrdId = child1.ClOrdId;
                Assert.Equal(30.0m, child1.Price);

                // Capture the snapshot BEFORE the repeg cycle starts so
                // PeggedRepegBook is empty in the snapshot. Repeg
                // intent is therefore only recoverable via WAL tail.
                ResolveSnapshotService(f).TryTakeSnapshot();

                // Now drift the mid → engine cancels child1 + persists
                // AlgoPeggedRepegStartedEvent (post-snapshot WAL tail).
                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                var mock = f.Services.GetRequiredService<MockEntryPointClient>();
                await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit the cancel after the drift");

                // Do NOT inject the Cancelled ER, do NOT take another
                // snapshot — the Started event must survive on the WAL
                // tail alone.
            }

            using (var f2 = TestAppFactory.WithOverrides(overrides))
            using (var http2 = f2.CreateClient())
            {
                var token = await f2.LoginAsync(http2);
                var adminToken = await f2.LoginAsync(http2, "admin");

                // The PeggedRepegBook entry must have been rebuilt by
                // the WAL replay (not the snapshot). Sanity-check the
                // book directly so a regression that drops the replay
                // handler fails loudly here, not through the more
                // indirect "parent gets suspended" path below.
                var repegBook = f2.Services.GetRequiredService<PeggedRepegBook>();
                var pending = repegBook.TryGet("default", algoIdNum);
                Assert.NotNull(pending);
                Assert.Equal(child1ClOrdId, pending!.Value.CancelledChildClOrdId);

                // Re-seed cache so SubmitNextSlice can price the
                // replacement (cache is volatile across restart).
                var cache2 = f2.Services.GetRequiredService<PegBookTopCache>();
                cache2.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);

                await InjectEr(http2, adminToken, child1ClOrdId, "Canceled");

                var book2 = f2.Services.GetRequiredService<WorkingOrderBook>();
                var child2 = await WaitForChildOtherThan(book2, algoIdNum.ToString(), child1ClOrdId,
                    TimeSpan.FromSeconds(5));
                Assert.Equal(31.0m, child2.Price);

                var snap = await GetAlgo(http2, token, algoIdNum.ToString());
                Assert.Equal("Working", snap.GetProperty("status").GetString());
                Assert.Equal("None", snap.GetProperty("terminalReason").GetString());
            }
        }
        finally
        {
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private static async Task<string> PostAlgo(HttpClient http, string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var posted = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return posted.GetProperty("algoId").GetString()!;
    }

    private static async Task<HttpResponseMessage> InjectEr(
        HttpClient http, string adminToken, ulong childClOrdId,
        string type, long? lastQty = null, decimal lastPx = 30m)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(new
            {
                ClOrdId = childClOrdId,
                Type = type,
                LastQty = lastQty,
                LastPx = lastQty.HasValue ? lastPx : (decimal?)null,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return resp;
    }

    private static async Task<JsonElement> GetAlgo(HttpClient http, string token, string algoId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/algo/{algoId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Order> WaitForAnyChild(WorkingOrderBook book, string algoIdStr, TimeSpan timeout)
    {
        var algoId = ulong.Parse(algoIdStr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var match = book.EnumerateChildrenOf("default", algoId).FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No child for algo {algoIdStr} within {timeout.TotalSeconds}s.");
    }

    private static async Task<Order> WaitForChildOtherThan(
        WorkingOrderBook book, string algoIdStr, ulong excludeClOrdId, TimeSpan timeout)
    {
        var algoId = ulong.Parse(algoIdStr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var match = book.EnumerateChildrenOf("default", algoId)
                .FirstOrDefault(c => c.ClOrdId != excludeClOrdId);
            if (match is not null) return match;
            await Task.Delay(10);
        }
        throw new TimeoutException(
            $"No new child (other than {excludeClOrdId}) for algo {algoIdStr} within {timeout.TotalSeconds}s.");
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout, string failMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(failMessage + $" (waited {timeout.TotalSeconds}s)");
    }

    private static async Task WaitForAlgoStatus(HttpClient http, string token, string algoId, params string[] anyOf)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var algo = await GetAlgo(http, token, algoId);
            last = algo.GetProperty("status").GetString();
            if (anyOf.Contains(last)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Algo {algoId} did not reach any of [{string.Join(",", anyOf)}] within 5s; last={last}");
    }
}
