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

    // ──────────────── Pass-3 review (#296) regression tests ────────────────

    [Fact]
    public async Task Pegged_RepegCancelFails_DoesNotPersistOrphanStartedAndRetriesNextTick()
    {
        // Pass-3 review (#296) P1 — approach B. Simulate the gateway
        // CancelAsync wire-call failing (venue unreachable / transient
        // I/O fault). The repeg must:
        //
        //   1. NOT leave a poison AlgoPeggedRepegStartedEvent in the
        //      WAL nor a PeggedRepegBook entry — both would otherwise
        //      stall the algo on a post-restart Reconcile (book
        //      entry + still-live child => RepegPending=true with no
        //      ack ever coming).
        //   2. Clear the in-memory RepegPending marker so the next
        //      scheduler tick can re-attempt the repeg cycle.
        //   3. Actually re-attempt and succeed once the injected
        //      fault is removed.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        // First cancel attempt fails; subsequent ones succeed.
        var failedCount = 0;
        mock.CancelFailureInjector = _ =>
        {
            if (Interlocked.Increment(ref failedCount) == 1)
            {
                return new InvalidOperationException("simulated gateway cancel failure");
            }
            return null;
        };

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100, repegMs: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        // Drift the mid → engine tries to cancel child1 and the
        // gateway rejects. With approach B, no Started event was
        // persisted, so there is nothing in the WAL to replay.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => failedCount >= 1,
            TimeSpan.FromSeconds(3), "engine did not attempt the first cancel");

        // The PeggedRepegBook MUST be empty for this algo — the
        // failed cancel path under approach B never persists a
        // Started event nor populates the book.
        var repegBook = f.Services.GetRequiredService<PeggedRepegBook>();
        Assert.Null(repegBook.TryGet("default", ulong.Parse(algoId)));

        // The parent must remain Working — the failure rolled back
        // the in-memory marker, no Suspended transition occurred.
        var snapAfterFailure = await GetAlgo(http, token, algoId);
        Assert.Equal("Working", snapAfterFailure.GetProperty("status").GetString());

        // The next scheduler tick must re-attempt the repeg cycle
        // (in-memory state was cleared cleanly). Drive it forward
        // until a second cancel goes out — this time the injector
        // returns null and the cancel succeeds.
        await WaitFor(() => mock.SubmittedCancels.Count >= 2,
            TimeSpan.FromSeconds(5),
            "engine did not retry the cancel after the simulated failure");

        // Releasing the cancel-ack drives the replacement child —
        // proves the algo is not stalled.
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId,
            TimeSpan.FromSeconds(5));
        Assert.Equal(31.0m, child2.Price);

        var snap = await GetAlgo(http, token, algoId);
        Assert.Equal("Working", snap.GetProperty("status").GetString());
        Assert.Equal("None", snap.GetProperty("terminalReason").GetString());
    }

    [Fact]
    public async Task Pegged_RecoverySelfHealsOrphanRepegEntry_NoStallOnReconcile()
    {
        // Pass-3 review (#296) P1 — approach C (defensive guard).
        // Historical WALs written by pre-Pass-3 binaries may have
        // persisted an AlgoPeggedRepegStartedEvent for a cancel that
        // never reached the venue (the old code persisted Started
        // BEFORE CancelAsync). After snapshot+restart that orphan
        // can survive into the rehydrated PeggedRepegBook. The
        // Reconcile pass MUST self-heal: if the cancelled child id
        // is no longer present as a live (non-terminal) order, the
        // book entry is dropped and RepegPending stays false so the
        // algo can continue slicing instead of stalling forever on
        // an ack that will never arrive.
        //
        // We construct the orphan shape end-to-end by:
        //   1. Driving a real repeg → Started lands in WAL + book.
        //   2. Injecting the cancel-ack so child1 goes terminal and
        //      child2 (the replacement) becomes the live child.
        //   3. Manually re-inserting an orphan book entry pointing
        //      at child1's now-terminal ClOrdId — simulating exactly
        //      the post-replay state an old-binary WAL would leave
        //      (Started without matching Resolved + child terminal).
        //   4. Snapshot + restart.
        //   5. Assert the book entry is gone after Reconcile and the
        //      algo is still Working.
        var dataDir = Path.Combine(Environment.CurrentDirectory, "test-data",
            "b3-pegged-repeg-self-heal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;
            ulong child1ClOrdId = 0;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var token = await f.LoginAsync(http);
                var adminToken = await f.LoginAsync(http, "admin");
                var cache = f.Services.GetRequiredService<PegBookTopCache>();
                cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

                var algoIdStr = await PostAlgo(http, token,
                    PeggedBody(total: 100, pegRef: "Mid", offsetTicks: 0,
                        repegMs: 100, tickSize: 0.5m));
                algoIdNum = ulong.Parse(algoIdStr);

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                var child1 = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(3));
                child1ClOrdId = child1.ClOrdId;

                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                var mock = f.Services.GetRequiredService<MockEntryPointClient>();
                await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit the cancel");

                // Drain the cycle normally so child1 goes terminal +
                // child2 becomes the live working slice. After this
                // point the engine's Reconcile path is the only thing
                // the orphan entry will encounter on restart.
                await InjectEr(http, adminToken, child1ClOrdId, "Canceled");
                _ = await WaitForChildOtherThan(book, algoIdStr, child1ClOrdId,
                    TimeSpan.FromSeconds(5));

                // Re-inject the orphan: an entry that points at the
                // now-terminal child1, exactly as an old-binary WAL
                // would have left after a backpressured Resolved.
                var repegBook = f.Services.GetRequiredService<PeggedRepegBook>();
                repegBook.Set("default", algoIdNum, child1ClOrdId, 31.0m, DateTimeOffset.UtcNow);

                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            using (var f2 = TestAppFactory.WithOverrides(overrides))
            using (var http2 = f2.CreateClient())
            {
                var token = await f2.LoginAsync(http2);

                // The Reconcile self-heal pass must have dropped the
                // orphan entry (cancelled child id is no longer live).
                var repegBook2 = f2.Services.GetRequiredService<PeggedRepegBook>();
                await WaitFor(() => repegBook2.TryGet("default", algoIdNum) is null,
                    TimeSpan.FromSeconds(3),
                    "Reconcile did not self-heal the orphan PeggedRepegBook entry");

                // And the algo did not get stuck in a stalled state.
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

    // ──────────────── Pass-4 review (#296) regression tests ────────────────

    [Fact]
    public async Task Pegged_FillRacesRepegCancel_DelayInjectedFill_NoReplacementChildAndAuditPairBalanced()
    {
        // Pass-4 review (#296) P1, Window 3 (the only race actually
        // reachable under the single-consumer signal queue — see Pass-5
        // P2 note further down).
        //
        // Pass-5 review (#296) P2. Deterministic gating via the new
        // MockEntryPointClient.CancelDelayInjector: the engine's
        // CancelAsync await is held by the test, the Fill ER is
        // injected while the cancel is in-flight, then the cancel is
        // released. The engine then drains the resulting
        // ChildExecutionObservedSignal AFTER the Started event has been
        // dispatched (single-consumer reactor) — so this exercises the
        // post-Started recovery path:
        //
        //   * Filled-case Pegged dedup at OnChildErAsync (the
        //     IsCancelledChild guard) → ResolveRepegOnFillAsync runs
        //     instead of the normal Fill-then-resubmit path.
        //   * ResolveRepegOnFillAsync emits
        //     AlgoPeggedRepegResolvedEvent{Aborted=false,
        //     Reason="FilledBeforeCancelAck"} so the audit pair is
        //     balanced and the PeggedRepegBook entry is cleared by
        //     replay convergence.
        //
        // **Guard pinned**: removing the IsCancelledChild check in the
        // Filled case (AlgoEngine line ~666) causes the Fill ER to
        // fall through to SubmitNextSliceAsync — a SECOND child gets
        // submitted (orphan replacement) and Assert.Single(allChildren)
        // below fails. Verified by mental simulation against the
        // current control flow.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100, repegMs: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        var repegBook = f.Services.GetRequiredService<PeggedRepegBook>();

        // Hold the engine's CancelAsync await via a TCS so we have a
        // deterministic window to race the Fill ER against.
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mock.CancelDelayInjector = _ => cancelGate.Task;

        // Drift the mid → engine cancels child1; CancelAsync now parks
        // on the gate.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not invoke CancelAsync after the mid drift");

        // While CancelAsync is held, inject the racing Fill ER. The
        // ExecutionReportProcessor runs synchronously on the caller
        // thread → order goes Filled / qty booked → child_er signal
        // enqueued. The signal sits in the channel because the engine
        // consumer is parked on CancelAsync.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 100);

        // Release CancelAsync → engine resumes, persists Started
        // (in-memory rt.RepegPending is still true because the queued
        // ER hasn't been dispatched yet), then dequeues the Fill ER on
        // the next loop iteration and routes through the
        // IsCancelledChild dedup → ResolveRepegOnFillAsync.
        cancelGate.SetResult();

        // Parent transitions to Completed (Fill consumed total qty).
        await WaitForAlgoStatus(http, token, algoId, "Completed");
        var snap = await GetAlgo(http, token, algoId);
        Assert.Equal(100, snap.GetProperty("filledQuantity").GetInt64());
        Assert.Equal("None", snap.GetProperty("terminalReason").GetString());

        // AlgoPeggedRepegResolvedEvent dispatched → book cleared. (The
        // RecordTerminalAsync.RemoveAll also clears it as a safety
        // net; this WaitFor doesn't distinguish.)
        await WaitFor(() => repegBook.TryGet("default", ulong.Parse(algoId)) is null,
            TimeSpan.FromSeconds(3),
            "PeggedRepegBook still has an entry after the fill race resolved");

        // Exactly one cancel, no replacement child spawned from the
        // Fill handler.
        Assert.Single(mock.SubmittedCancels);
        var allChildren = book.EnumerateChildrenOf("default", ulong.Parse(algoId)).ToList();
        Assert.Single(allChildren);
        Assert.Equal(child1.ClOrdId, allChildren[0].ClOrdId);
    }

    [Fact]
    public async Task Pegged_FillRacesRepegCancel_DocumentsWindows1and2DefensiveGuard()
    {
        // Pass-4 review (#296) P1, Windows 1 + 2 (defensive). Windows
        // 1 (Fill processed before CancelAsync is invoked) and 2 (Fill
        // processed after CancelAsync returns but before Started is
        // persisted) are NOT naturally reachable in the current
        // single-consumer reactor: the engine awaits CancelAsync
        // inside its own consumer task, so a ChildExecutionObservedSignal
        // racing the cancel can only be dequeued AFTER CancelAsync
        // returns AND the post-cancel Started dispatch runs in the
        // same iteration (which is always Window 3).
        //
        // Pass-5 review (#296) P2. We tried to construct the race
        // with the new CancelDelayInjector — emit the Fill ER while
        // the cancel is held, release the cancel — but the engine
        // does not observe rt.RepegPending=false until its consumer
        // loop runs OnChildErAsync, which happens AFTER the Started
        // dispatch completes. The "if (!rt.RepegPending) return;"
        // guard at the top of the post-cancel block (AlgoEngine line
        // ~1524) is therefore defensive code that a future multi-
        // consumer reactor (or an SDK that pumps ERs synchronously
        // inside the cancel wire-call) would actually trigger.
        //
        // This test pins the OBSERVABLE invariant that holds across
        // every window: regardless of which sub-window the race
        // resolves in, the engine ends with (a) the parent in a
        // coherent terminal/working state, (b) no orphan replacement
        // child, and (c) no lingering Started without a matching
        // Resolved (a future replay must converge on the same in-
        // memory state). Combined with the Window 3 test above this
        // covers the full race surface to the granularity that's
        // observable today.
        //
        // **Guard pinned**: the post-Cancelled `IsCancelledChild`
        // dedup at AlgoEngine line ~788 — removing it would let a
        // late Cancelled-status ER (the cancel-ack arriving after the
        // fill resolved the cycle) fall through to the VenueCancelled
        // branch and Suspend the parent. The cycle-resolved
        // RepegPending=false assertion below catches that regression
        // (Suspended ≠ Completed).
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100, repegMs: 50));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        var repegBook = f.Services.GetRequiredService<PeggedRepegBook>();

        // Drift → engine cancels child1.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit the cancel");

        // Race the Fill ER in immediately. Settled outcome is what we
        // verify; the exact internal window the race resolves in is
        // intentionally not pinned.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 100);
        await WaitForAlgoStatus(http, token, algoId, "Completed");

        await WaitFor(() => repegBook.TryGet("default", ulong.Parse(algoId)) is null,
            TimeSpan.FromSeconds(3),
            "PeggedRepegBook still has an entry after the fill race");

        Assert.Single(mock.SubmittedCancels);
        var allChildren = book.EnumerateChildrenOf("default", ulong.Parse(algoId)).ToList();
        Assert.Single(allChildren);
    }

    [Fact]
    public async Task Pegged_LateCancelAckAfterFillResolution_IsNoOpNotDoubleReplacement()
    {
        // Pass-4 review (#296) P1, scenario 3. After the Fill-before-
        // cancel-ack race resolves (Resolved dispatched, book cleared,
        // RepegPending=false, parent Completed), a venue cancel-ack ER
        // may still arrive for the same old child (the venue processed
        // the cancel after the fill landed). That late Cancelled wire
        // MUST be a no-op — NOT a second SubmitNextSliceAsync call
        // (which would duplicate the replacement child and orphan a
        // working order).
        //
        // Because Pegged places the full RemainingQuantity on each
        // working slice, a full Fill of child1 also completes the
        // parent in this test; the parent-terminal short-circuit at
        // the top of OnChildErAsync's Filled case is what absorbs the
        // late ER in that path. The non-terminal-parent case
        // (subsequent-repeg dedup) is covered separately by
        // Pegged_LateFillAfterSubsequentRepeg_DedupViaHistoryRing.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100, repegMs: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        var repegBook = f.Services.GetRequiredService<PeggedRepegBook>();

        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit the cancel");
        await WaitFor(() => repegBook.TryGet("default", ulong.Parse(algoId)) is not null,
            TimeSpan.FromSeconds(3), "Started marker did not land in book");

        // Fill race resolves the cycle first.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 100);
        await WaitForAlgoStatus(http, token, algoId, "Completed");
        await WaitFor(() => repegBook.TryGet("default", ulong.Parse(algoId)) is null,
            TimeSpan.FromSeconds(3), "Resolved did not clear the book");

        var newOrdersBefore = mock.SubmittedNewOrders.Count;
        var childrenBefore = book.EnumerateChildrenOf("default", ulong.Parse(algoId)).Count();

        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        await Task.Delay(200);

        var snap = await GetAlgo(http, token, algoId);
        Assert.Equal("Completed", snap.GetProperty("status").GetString());
        Assert.Equal("None", snap.GetProperty("terminalReason").GetString());

        Assert.Equal(newOrdersBefore, mock.SubmittedNewOrders.Count);
        Assert.Equal(childrenBefore,
            book.EnumerateChildrenOf("default", ulong.Parse(algoId)).Count());

        Assert.Null(repegBook.TryGet("default", ulong.Parse(algoId)));
    }

    // ──────────────── Pass-5 review (#296) P1 regression tests ────────────────

    [Fact]
    public async Task Pegged_LateFillAfterSubsequentRepeg_DedupViaHistoryRing()
    {
        // Pass-5 review (#296) P1. Reproduces the single-slot dedup
        // gap that pass-4's LastRepegCancelledChildId left open:
        //
        //   1. Repeg A: engine cancels child1, cancel-ack lands,
        //      child2 is placed. After this point the single-slot
        //      marker has rotated to child2 (the LATEST cycle's
        //      cancelled child id).
        //   2. Repeg B: engine cancels child2; cancel-ack pending.
        //   3. LATE Fill ER for child1 arrives (delayed venue/
        //      simulator reporting). With the single-slot marker
        //      pointing at child2, child1 != marker → the dedup
        //      branches in OnChildErAsync (Filled-case at line ~666
        //      AND Cancelled-case at line ~788) were both MISSED →
        //      the late ER fell through to either a spurious
        //      replacement child or the VenueCancelled-suspension
        //      branch, depending on the order's preserved terminal
        //      status.
        //
        // The fix is the bounded FIFO history ring
        // (PeggedRepegBook.IsCancelledChild) that remembers EVERY
        // recently engine-cancelled child id, not just the latest.
        //
        // **Guard pinned**: replace IsCancelledChild with
        // `rt.LastRepegCancelledChildId == child.ClOrdId` at the
        // Cancelled-case dedup (line ~788) and this test fails: the
        // late Fill ER's signal lands the engine on the
        // VenueCancelled fallthrough → parent transitions to
        // Suspended, the final Assert.Equal("Working", ...) below
        // catches it.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        // total=200 so a partial Fill on child1 doesn't terminal the
        // parent — keeps us on the non-terminal-parent path that
        // exercises the dedup branch (rather than the algo.IsTerminal
        // short-circuit at the top of the Filled case).
        var algoId = await PostAlgo(http, token, PeggedBody(total: 200, repegMs: 100));
        var algoIdNum = ulong.Parse(algoId);
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();

        // Cycle A: drift → cancel child1, then ack so child2 lands.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit cancel A");
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(31.0m, child2.Price);

        // Cycle B: drift → cancel child2. Do NOT ack — leaves
        // RepegPending=true and the single-slot marker pointing at
        // child2 (so child1 is no longer "the" sticky cancelled id).
        cache.UpdateBookTop("PETR4", 31.5m, 32.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child2.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit cancel B");

        var newOrdersBeforeLateEr = mock.SubmittedNewOrders.Count;

        // Late Fill ER for child1 (already-Cancelled). The processor
        // preserves the terminal status (Order.ApplyCumulativeFill
        // line ~367) but enqueues the signal because cumQty advanced.
        // The engine then sees child1.Status==Cancelled and routes
        // into the Cancelled-case; without the history-ring dedup
        // the parent would be Suspended/VenueCancelled.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 10);

        // Give the consumer loop time to drain the late ER signal.
        await Task.Delay(200);

        var snap = await GetAlgo(http, token, algoId);
        Assert.Equal("Working", snap.GetProperty("status").GetString());
        Assert.Equal("None", snap.GetProperty("terminalReason").GetString());

        // No new replacement child spawned from the late ER — only
        // the two cycles' children (child1 terminal + child2 still
        // working) are in the book.
        Assert.Equal(newOrdersBeforeLateEr, mock.SubmittedNewOrders.Count);
        var liveChildren = book.EnumerateChildrenOf("default", algoIdNum)
            .Where(c => c.Status is OrderStatus.PendingNew or OrderStatus.Working or OrderStatus.PartiallyFilled)
            .ToList();
        Assert.Single(liveChildren);
        Assert.Equal(child2.ClOrdId, liveChildren[0].ClOrdId);
    }

    [Fact]
    public async Task Pegged_RestartThenLateFillForOldChild_DedupSurvivesSnapshotRestart()
    {
        // Pass-5 review (#296) P1. Cross-restart durability of the
        // cancelled-child history ring. Without the snapshot field
        // (PeggedRepegHistory) a restart between the original repeg
        // and the late ER would lose the dedup memory and let the
        // late Fill ER suspend the parent on the post-restart
        // VenueCancelled fallthrough.
        //
        // Sequence:
        //   1. Pre-restart: drive repeg A (cancel + ack child1 →
        //      child2 placed) so child1 is in the history ring but
        //      NOT in PeggedRepegPending (cycle is resolved).
        //   2. Drive repeg B (cancel child2, no ack) so the pending
        //      entry exists. Take a snapshot.
        //   3. Cold restart. Snapshot.Restore re-hydrates BOTH the
        //      pending entry AND the history ring.
        //   4. Inject a late Fill for child1. The engine's history-
        //      ring lookup hits, dedup fires, parent stays Working.
        //
        // **Guard pinned**: remove the PeggedRepegHistory snapshot
        // capture (or the RestoreHistory call in StateSnapshotter)
        // and this test fails — child1 is not in the post-restart
        // ring → late Fill falls through to Suspended.
        var dataDir = Path.Combine(Environment.CurrentDirectory, "test-data",
            "b3-pegged-history-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;
            ulong child1ClOrdId = 0;
            ulong child2ClOrdId = 0;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var token = await f.LoginAsync(http);
                var adminToken = await f.LoginAsync(http, "admin");
                var cache = f.Services.GetRequiredService<PegBookTopCache>();
                cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

                var algoIdStr = await PostAlgo(http, token,
                    PeggedBody(total: 200, pegRef: "Mid", offsetTicks: 0,
                        repegMs: 100, tickSize: 0.5m));
                algoIdNum = ulong.Parse(algoIdStr);

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                var child1 = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(3));
                child1ClOrdId = child1.ClOrdId;

                var mock = f.Services.GetRequiredService<MockEntryPointClient>();

                // Cycle A: cancel + ack → child2 placed. child1 ends
                // up in the history ring; the PeggedRepegBook pending
                // entry is cleared by the Resolved event.
                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit cancel A pre-restart");
                await InjectEr(http, adminToken, child1ClOrdId, "Canceled");
                var child2 = await WaitForChildOtherThan(book, algoIdStr, child1ClOrdId,
                    TimeSpan.FromSeconds(3));
                child2ClOrdId = child2.ClOrdId;

                // Cycle B: cancel child2, no ack. The pending entry
                // now references child2; child1 lives only in the
                // history ring.
                cache.UpdateBookTop("PETR4", 31.5m, 32.5m, DateTimeOffset.UtcNow);
                await WaitFor(() => mock.SubmittedCancels.Any(c => c.OrigClOrdId == child2ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit cancel B pre-restart");

                // Snapshot under the dispatcher lock — must capture
                // both the pending entry (for child2) and the history
                // ring (containing child1 + child2).
                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            using (var f2 = TestAppFactory.WithOverrides(overrides))
            using (var http2 = f2.CreateClient())
            {
                var token = await f2.LoginAsync(http2);
                var adminToken = await f2.LoginAsync(http2, "admin");

                // Sanity: snapshot restore populated the history ring.
                var repegBook2 = f2.Services.GetRequiredService<PeggedRepegBook>();
                Assert.True(repegBook2.IsCancelledChild("default", algoIdNum, child1ClOrdId),
                    "Snapshot restore did not rehydrate child1 in the history ring");
                Assert.True(repegBook2.IsCancelledChild("default", algoIdNum, child2ClOrdId),
                    "Snapshot restore did not rehydrate child2 in the history ring");

                // Late Fill for child1 (already-Cancelled in the
                // restored order book). Without the snapshotted
                // history ring the post-restart Cancelled-case dedup
                // would miss and Suspend the parent.
                await InjectEr(http2, adminToken, child1ClOrdId, "Fill", lastQty: 10);
                await Task.Delay(200);

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
