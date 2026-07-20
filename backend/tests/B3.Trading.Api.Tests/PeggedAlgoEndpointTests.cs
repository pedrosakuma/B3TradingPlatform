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

        // #300 retrofit. Engine must enqueue exactly one cancel-replace
        // for the live child (preserving venue time-priority) — not a
        // bare cancel + new-order.
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3),
            "engine never issued CancelReplace for the live child after mid moved");
        Assert.Empty(mock.SubmittedCancels);

        // Mirror what the venue would do — emit the Replaced ER for
        // the cancel-replace request. The processor hydrates the new
        // child into the book at the fresh target price.
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1.ClOrdId);
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);

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
        Assert.Empty(mock.SubmittedReplaces);
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

        // First move clamps to the limit (30.5) — one repeg via
        // cancel-replace (#300 retrofit).
        cache.UpdateBookTop("PETR4", 31.5m, 32.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedReplaces.Count == 1,
            TimeSpan.FromSeconds(3), "engine did not repeg to the price limit");
        var replace1 = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1.ClOrdId);
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace1.NewClOrdId,
            origClOrdId: replace1.OriginalClOrdId,
            leavesQuantity: replace1.NewQuantity);
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.5m, child2.Price);

        // Subsequent further-aggressive moves stay clamped at 30.5,
        // identical to child2.Price → IsRepegNeeded false → engine
        // must not enqueue another replace.
        for (int i = 0; i < 6; i++)
        {
            cache.UpdateBookTop("PETR4", 32.5m + i, 33.5m + i, DateTimeOffset.UtcNow);
            await Task.Delay(50);
        }
        Assert.Single(mock.SubmittedReplaces);
        Assert.Empty(mock.SubmittedCancels);
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
            ["Trading:Exchange:Firms:0:FirmId"] = "default",
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
        // #300 retrofit. Repeg replace lands → engine adopts new
        // child via the Replaced ER. A defensive duplicate / late
        // Cancelled ER for the SAME old child must still be a no-op
        // (the cancelled-child dedup ring keeps it from routing
        // through the VenueCancelled branch). The replacement child
        // stays live and the parent stays Working.
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

        // Drift the mid → engine issues cancel-replace targeting child1.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine never issued CancelReplace for child1 after mid moved");

        // Replaced ER → adoption path drives Resolved + new child.
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1.ClOrdId);
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId, TimeSpan.FromSeconds(3));
        Assert.Equal(31.0m, child2.Price);

        // Spurious / late Cancelled ER for the OLD child must be a
        // no-op (dedup-ring hit) — parent stays Working, no terminal
        // published.
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

        // First mid move → first cancel-replace (do NOT inject the
        // Replaced ER yet).
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit the first cancel-replace");

        // Burst of further mid moves spanning well past RepegInterval.
        // The RepegPending short-circuit must NOT enqueue another
        // cancel-replace for child1 while the ack is outstanding.
        for (int i = 0; i < 4; i++)
        {
            cache.UpdateBookTop("PETR4", 30.5m + i * 0.1m, 31.5m + i * 0.1m,
                DateTimeOffset.UtcNow);
            await Task.Delay(60);
        }
        Assert.Single(mock.SubmittedReplaces);
        Assert.Empty(mock.SubmittedCancels);

        // Now release the cycle: the Replaced ER lands and the
        // engine adopts the replacement child.
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1.ClOrdId);
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId, TimeSpan.FromSeconds(3));
        Assert.NotNull(child2);

        // The parent must remain Working — a spurious / late Cancelled
        // ER for child1 must not flip it Suspended (the dedup-ring
        // marker keeps the classification correct even post-resolve).
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
        // #300 retrofit. Engine emits the repeg cancel-replace but
        // crashes before the venue Replaced ER lands. After
        // snapshot+restart the OrderReplaceRequestedEvent replay
        // re-hydrates the PendingReplacementRegistry intent and the
        // AlgoPeggedRepegStartedEvent replay re-hydrates the
        // PeggedRepegBook entry, so the Replaced ER that finally
        // lands is adopted via the engine's normal adoption block
        // (NOT the VenueCancelled-suspension branch).
        var dataDir = Path.Combine(Environment.CurrentDirectory, "test-data",
            "b3-pegged-repeg-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;
            ulong child1ClOrdId = 0;
            ulong replaceNewClOrdId = 0;
            long replaceNewQty = 0;

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

                // Drift the mid → engine emits cancel-replace. DO NOT
                // inject the Replaced ER — replace is in flight at
                // snapshot time.
                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                var mock = f.Services.GetRequiredService<MockEntryPointClient>();
                await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit the cancel-replace before snapshot");

                var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1ClOrdId);
                replaceNewClOrdId = replace.NewClOrdId;
                replaceNewQty = replace.NewQuantity;

                // Capture the snapshot with the replace still pending.
                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            // Cold restart — registry + repeg book + working child
            // are rehydrated from snapshot/WAL replay.
            using (var f2 = TestAppFactory.WithOverrides(overrides))
            using (var http2 = f2.CreateClient())
            {
                var token = await f2.LoginAsync(http2);
                var adminToken = await f2.LoginAsync(http2, "admin");

                // Inject the Replaced ER the engine was waiting for.
                await InjectReplacedEr(http2, adminToken,
                    newClOrdId: replaceNewClOrdId,
                    origClOrdId: child1ClOrdId,
                    leavesQuantity: replaceNewQty);

                // Replacement child must appear and parent must NOT
                // be Suspended.
                var book2 = f2.Services.GetRequiredService<WorkingOrderBook>();
                var child2 = await WaitForChildOtherThan(book2, algoIdNum.ToString(), child1ClOrdId,
                    TimeSpan.FromSeconds(5));
                Assert.Equal(replaceNewClOrdId, child2.ClOrdId);

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
            ulong replaceNewClOrdId = 0;
            long replaceNewQty = 0;

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

                // Now drift the mid → engine emits cancel-replace +
                // OrderReplaceRequestedEvent + AlgoPeggedRepegStartedEvent
                // (all post-snapshot WAL tail).
                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                var mock = f.Services.GetRequiredService<MockEntryPointClient>();
                await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit the cancel-replace after the drift");

                var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1ClOrdId);
                replaceNewClOrdId = replace.NewClOrdId;
                replaceNewQty = replace.NewQuantity;

                // Do NOT inject the Replaced ER, do NOT take another
                // snapshot — both Started and ReplaceRequested events
                // must survive on the WAL tail alone.
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
                // indirect adoption path below.
                var repegBook = f2.Services.GetRequiredService<PeggedRepegBook>();
                var pending = repegBook.TryGet("default", algoIdNum);
                Assert.NotNull(pending);
                Assert.Equal(child1ClOrdId, pending!.Value.CancelledChildClOrdId);

                await InjectReplacedEr(http2, adminToken,
                    newClOrdId: replaceNewClOrdId,
                    origClOrdId: child1ClOrdId,
                    leavesQuantity: replaceNewQty);

                var book2 = f2.Services.GetRequiredService<WorkingOrderBook>();
                var child2 = await WaitForChildOtherThan(book2, algoIdNum.ToString(), child1ClOrdId,
                    TimeSpan.FromSeconds(5));
                Assert.Equal(replaceNewClOrdId, child2.ClOrdId);

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
    public async Task Pegged_RepegReplaceSendAmbiguous_RetainsIntent_NoOrphanStarted()
    {
        // #300 retrofit. Simulate the gateway CancelReplaceAsync wire-
        // call failing (venue unreachable / transient I/O fault). The
        // send is AMBIGUOUS: the venue may have already accepted the
        // replace and the Replaced ER may still land later. Therefore
        // the engine must:
        //
        //   1. NOT persist an AlgoPeggedRepegStartedEvent (no Started
        //      ⇒ no orphan WAL Started without a matching Resolved).
        //   2. NOT populate PeggedRepegBook (same reason — Reconcile
        //      would otherwise stall the algo on restart).
        //   3. KEEP the PendingReplacementRegistry intent in place
        //      (and held margin) so a late Replaced ER can still
        //      converge to the new child. AlgoScheduler.SweepAmbiguousReplaceIntents
        //      bounds the leak via TTL.
        //   4. Bump <c>algo.modify_send_ambiguous_total</c> for ops
        //      visibility (tagged algoType=pegged).
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        // First replace attempt fails; subsequent ones (none expected
        // under the new semantics) would succeed.
        var failedCount = 0;
        mock.ReplaceFailureInjector = _ =>
        {
            if (Interlocked.Increment(ref failedCount) == 1)
            {
                return new InvalidOperationException("simulated gateway replace failure");
            }
            return null;
        };

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100, repegMs: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        // Drift the mid → engine tries cancel-replace; gateway throws
        // (ambiguous send).
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => failedCount >= 1,
            TimeSpan.FromSeconds(3), "engine did not attempt the first cancel-replace");

        // The PeggedRepegBook MUST be empty for this algo — Started
        // event was deliberately not dispatched on the false return.
        var repegBook = f.Services.GetRequiredService<PeggedRepegBook>();
        Assert.Null(repegBook.TryGet("default", ulong.Parse(algoId)));

        // The intent IS retained in the registry (ambiguous send) so
        // a late Replaced ER can still converge. Verify via the
        // in-flight check on the OLD child id.
        var registry = f.Services.GetRequiredService<PendingReplacementRegistry>();
        Assert.True(registry.IsOriginalInFlight(child1.ClOrdId),
            "ambiguous-send must retain the replace intent indexed by original ClOrdID");

        // Parent stays Working — no Suspended transition.
        var snapAfter = await GetAlgo(http, token, algoId);
        Assert.Equal("Working", snapAfter.GetProperty("status").GetString());

        // The next scheduler tick must NOT spawn a second replace —
        // IsOriginalInFlight short-circuits TryReplaceChildAsync to
        // false with reason="already_in_flight" until the intent is
        // resolved or swept.
        await Task.Delay(400);
        Assert.Single(mock.SubmittedReplaces);
        Assert.Empty(mock.SubmittedCancels);
    }

    [Fact]
    public async Task Pegged_ParentCancelWithAmbiguousChild_FailsClosed()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        mock.ReplaceFailureInjector = _ =>
            new InvalidOperationException("simulated ambiguous replace send");
        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        var ledger = f.Services.GetRequiredService<
            B3.Trading.Application.Outbound.OutboundMutationLedger>();
        await WaitFor(
            () => ledger.GetAlgoMutations("default", ulong.Parse(algoId)).Any(m =>
                m.State == B3.Trading.Application.Outbound.OutboundMutationState.Ambiguous),
            TimeSpan.FromSeconds(3),
            "replace mutation did not become ambiguous");

        var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await http.SendAsync(cancel)).StatusCode);

        await Task.Delay(250);
        var snapshot = await GetAlgo(http, token, algoId);
        Assert.Equal("Working", snapshot.GetProperty("status").GetString());
        Assert.DoesNotContain(mock.SubmittedCancels, c => c.OrigClOrdId == child.ClOrdId);
    }

    [Fact]
    public async Task Pegged_ParentCancelDuringReplace_CancelsAdoptedChild()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedNewOrders.Any(order => order.ClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child was not dispatched");

        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(
            () => mock.SubmittedReplaces.Any(replace =>
                replace.OriginalClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "repeg replace was not dispatched");
        var replace = mock.SubmittedReplaces.Single(replace =>
            replace.OriginalClOrdId == child.ClOrdId);

        var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(cancel)).StatusCode);

        await InjectReplacedEr(
            http,
            adminToken,
            replace.NewClOrdId,
            replace.OriginalClOrdId,
            replace.NewQuantity);
        _ = await WaitForChildOtherThan(
            book,
            algoId,
            child.ClOrdId,
            TimeSpan.FromSeconds(3));
        await WaitFor(
            () => mock.SubmittedCancels.Any(request =>
                request.OrigClOrdId == replace.NewClOrdId),
            TimeSpan.FromSeconds(3),
            "adopted replacement child was not cancelled");

        var snapshot = await GetAlgo(http, token, algoId);
        Assert.Equal("Cancelling", snapshot.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Pegged_RejectedChildCancel_RedrivesWithFreshLogicalAction()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedNewOrders.Any(order => order.ClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child was not dispatched");

        var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(cancel)).StatusCode);
        await WaitFor(
            () => mock.SubmittedCancels.Any(request =>
                request.OrigClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child cancel was not dispatched");
        var firstCancel = mock.SubmittedCancels.Single(request =>
            request.OrigClOrdId == child.ClOrdId);

        mock.EmitExecutionReport(new ExecutionReportEnvelope(
            firstCancel.ClOrdId,
            EpExecType.Rejected,
            child.LeavesQuantity,
            child.CumulativeQuantity,
            0,
            0m,
            "too_late_to_cancel",
            child.ClOrdId));

        await WaitFor(
            () => mock.SubmittedCancels.Any(request =>
                request.OrigClOrdId == child.ClOrdId
                && request.ClOrdId != firstCancel.ClOrdId),
            TimeSpan.FromSeconds(3),
            "rejected child cancel was not redriven with a fresh ClOrdID");
        var secondCancel = mock.SubmittedCancels.Single(request =>
            request.OrigClOrdId == child.ClOrdId
            && request.ClOrdId != firstCancel.ClOrdId);
        Assert.NotEqual(firstCancel.ClOrdId, secondCancel.ClOrdId);

        var ledger = f.Services.GetRequiredService<
            B3.Trading.Application.Outbound.OutboundMutationLedger>();
        var origins = ledger.GetAlgoMutations("default", ulong.Parse(algoId))
            .Where(m => m.AlgoOriginIdentity?.ActionKind
                == B3.Trading.Application.Outbound.AlgoOutboundActionKind.CancelChild)
            .Select(m => m.AlgoOriginIdentity!.Sequence)
            .Order()
            .ToArray();
        Assert.Equal([0, 1], origins);
    }

    [Fact]
    public async Task Pegged_ProvenUnsentChildCancel_ExplicitRetryPreservesOriginAndCapsAttempts()
    {
        using var f = TestAppFactory.WithOverrides(
            Simulator(),
            services =>
            {
                services.RemoveAll<IExchangeGateway>();
                services.AddSingleton<ProvenUnsentCancelGateway>();
                services.AddSingleton<IExchangeGateway>(sp =>
                    sp.GetRequiredService<ProvenUnsentCancelGateway>());
            });
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedNewOrders.Any(order => order.ClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child was not dispatched");
        var gateway = f.Services.GetRequiredService<ProvenUnsentCancelGateway>();

        async Task<HttpStatusCode> DeleteAlgo()
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Accepted, await DeleteAlgo());
        var ledger = f.Services.GetRequiredService<
            B3.Trading.Application.Outbound.OutboundMutationLedger>();
        await WaitFor(
            () => ledger.GetAlgoMutations("default", ulong.Parse(algoId)).Any(m =>
                m.AlgoOriginIdentity?.ActionKind
                    == B3.Trading.Application.Outbound.AlgoOutboundActionKind.CancelChild
                && m.State
                    == B3.Trading.Application.Outbound.OutboundMutationState.ProvenUnsent),
            TimeSpan.FromSeconds(3),
            "initial child cancel did not become ProvenUnsent");

        Assert.Equal(HttpStatusCode.Accepted, await DeleteAlgo());
        await WaitFor(
            () => gateway.AttemptedCancelClOrdIds.Count == 2,
            TimeSpan.FromSeconds(3),
            "explicit ProvenUnsent retry was not dispatched");
        Assert.Equal(HttpStatusCode.Accepted, await DeleteAlgo());
        await Task.Delay(250);

        var cancelMutation = Assert.Single(
            ledger.GetAlgoMutations("default", ulong.Parse(algoId)),
            m => m.AlgoOriginIdentity?.ActionKind
                == B3.Trading.Application.Outbound.AlgoOutboundActionKind.CancelChild);
        Assert.Equal(2, cancelMutation.Attempts.Count);
        Assert.Equal(
            B3.Trading.Application.Outbound.OutboundMutationState.ProvenUnsent,
            cancelMutation.State);
        Assert.Equal(2, gateway.AttemptedCancelClOrdIds.Count);
        Assert.Equal(
            cancelMutation.AlgoOriginIdentity,
            ledger.GetAlgoMutations("default", ulong.Parse(algoId))
                .Single(m => m.MutationId == cancelMutation.MutationId)
                .AlgoOriginIdentity);
        Assert.NotEqual(
            gateway.AttemptedCancelClOrdIds.ElementAt(0),
            gateway.AttemptedCancelClOrdIds.ElementAt(1));
    }

    [Fact]
    public async Task Pegged_ProvenUnsentChildReplace_ExplicitRetryPreservesOriginAndCapsAttempts()
    {
        using var f = TestAppFactory.WithOverrides(
            Simulator(),
            services =>
            {
                services.RemoveAll<IExchangeGateway>();
                services.AddSingleton<ProvenUnsentCancelGateway>();
                services.AddSingleton<IExchangeGateway>(sp =>
                    sp.GetRequiredService<ProvenUnsentCancelGateway>());
            });
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedNewOrders.Any(order => order.ClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child was not dispatched");
        var gateway = f.Services.GetRequiredService<ProvenUnsentCancelGateway>();

        async Task<HttpStatusCode> ModifyChild()
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/algo/{algoId}/modify")
            {
                Content = JsonContent.Create(new
                {
                    ChildClOrdId = child.ClOrdId,
                    NewPrice = 29.9m,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Accepted, await ModifyChild());
        var ledger = f.Services.GetRequiredService<
            B3.Trading.Application.Outbound.OutboundMutationLedger>();
        await WaitFor(
            () => ledger.GetAlgoMutations("default", ulong.Parse(algoId)).Any(m =>
                m.AlgoOriginIdentity?.ActionKind
                    == B3.Trading.Application.Outbound.AlgoOutboundActionKind.ReplaceChild
                && m.State
                    == B3.Trading.Application.Outbound.OutboundMutationState.ProvenUnsent),
            TimeSpan.FromSeconds(3),
            "initial child replace did not become ProvenUnsent");

        Assert.Equal(HttpStatusCode.Accepted, await ModifyChild());
        await WaitFor(
            () => gateway.AttemptedReplaceClOrdIds.Count == 2,
            TimeSpan.FromSeconds(3),
            "explicit ProvenUnsent replace retry was not dispatched");
        Assert.Equal(HttpStatusCode.Accepted, await ModifyChild());
        await Task.Delay(250);

        var replaceMutation = Assert.Single(
            ledger.GetAlgoMutations("default", ulong.Parse(algoId)),
            m => m.AlgoOriginIdentity?.ActionKind
                == B3.Trading.Application.Outbound.AlgoOutboundActionKind.ReplaceChild);
        Assert.Equal(2, replaceMutation.Attempts.Count);
        Assert.Equal(
            B3.Trading.Application.Outbound.OutboundMutationState.ProvenUnsent,
            replaceMutation.State);
        Assert.Equal(2, gateway.AttemptedReplaceClOrdIds.Count);
        Assert.NotEqual(
            gateway.AttemptedReplaceClOrdIds.ElementAt(0),
            gateway.AttemptedReplaceClOrdIds.ElementAt(1));
    }

    [Fact]
    public async Task Pegged_ProvenUnsentRepeg_RetriesFrozenCommandWhenMarketRevertsToLiveChild()
    {
        using var f = TestAppFactory.WithOverrides(
            Simulator(),
            services =>
            {
                services.RemoveAll<IExchangeGateway>();
                services.AddSingleton<ProvenUnsentCancelGateway>();
                services.AddSingleton<IExchangeGateway>(sp =>
                    sp.GetRequiredService<ProvenUnsentCancelGateway>());
            });
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var gateway = f.Services.GetRequiredService<ProvenUnsentCancelGateway>();
        gateway.ProvenUnsentReplaceFailuresRemaining = 1;
        var algoId = await PostAlgo(
            http,
            token,
            PeggedBody(total: 100, repegMs: 1000));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedNewOrders.Any(order => order.ClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child was not dispatched");

        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(
            () => gateway.AttemptedReplaceClOrdIds.Count == 1,
            TimeSpan.FromSeconds(3),
            "initial repeg did not become ProvenUnsent");
        var frozenPrice = gateway.AttemptedReplacePrices.Single();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);
        await WaitFor(
            () => gateway.AttemptedReplaceClOrdIds.Count == 2,
            TimeSpan.FromSeconds(3),
            "ProvenUnsent repeg was not retried");
        await WaitFor(
            () => mock.SubmittedReplaces.Count == 1,
            TimeSpan.FromSeconds(3),
            "repeg retry did not self-heal through a successful transport write");

        var ledger = f.Services.GetRequiredService<
            B3.Trading.Application.Outbound.OutboundMutationLedger>();
        var repeg = Assert.Single(
            ledger.GetAlgoMutations("default", ulong.Parse(algoId)),
            m => m.AlgoOriginIdentity?.ActionKind
                == B3.Trading.Application.Outbound.AlgoOutboundActionKind.Repeg);
        Assert.Equal(2, repeg.Attempts.Count);
        Assert.Equal(
            B3.Trading.Application.Outbound.OutboundMutationState.TransportWriteCompleted,
            repeg.State);
        Assert.NotEqual(
            gateway.AttemptedReplaceClOrdIds.ElementAt(0),
            gateway.AttemptedReplaceClOrdIds.ElementAt(1));
        Assert.Equal(
            gateway.AttemptedReplaceClOrdIds.ElementAt(1),
            mock.SubmittedReplaces.Single().NewClOrdId);
        Assert.Equal([frozenPrice, frozenPrice], gateway.AttemptedReplacePrices);
        Assert.Equal(31m, frozenPrice);
        Assert.Equal(frozenPrice, mock.SubmittedReplaces.Single().NewPrice);
    }

    [Fact]
    public async Task Pegged_ProvenUnsentRepeg_OriginalChildCancelled_SuspendsInsteadOfWedging()
    {
        using var f = TestAppFactory.WithOverrides(
            Simulator(),
            services =>
            {
                services.RemoveAll<IExchangeGateway>();
                services.AddSingleton<ProvenUnsentCancelGateway>();
                services.AddSingleton<IExchangeGateway>(sp =>
                    sp.GetRequiredService<ProvenUnsentCancelGateway>());
            });
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(
            http,
            token,
            PeggedBody(total: 100, repegMs: 1000));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedNewOrders.Any(order => order.ClOrdId == child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child was not dispatched");

        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        var ledger = f.Services.GetRequiredService<
            B3.Trading.Application.Outbound.OutboundMutationLedger>();
        await WaitFor(
            () => ledger.GetAlgoMutations("default", ulong.Parse(algoId)).Any(m =>
                m.AlgoOriginIdentity?.ActionKind
                    == B3.Trading.Application.Outbound.AlgoOutboundActionKind.Repeg
                && m.State
                    == B3.Trading.Application.Outbound.OutboundMutationState.ProvenUnsent),
            TimeSpan.FromSeconds(3),
            "initial repeg did not become ProvenUnsent");

        var repegBook = f.Services.GetRequiredService<PeggedRepegBook>();
        await WaitFor(
            () => !repegBook.IsCancelledChild("default", ulong.Parse(algoId), child.ClOrdId),
            TimeSpan.FromSeconds(3),
            "ProvenUnsent repeg retained its optimistic cancelled-child marker");

        await InjectEr(http, adminToken, child.ClOrdId, "Canceled");
        await WaitForAlgoStatus(http, token, algoId, "Suspended");
        var snapshot = await GetAlgo(http, token, algoId);
        Assert.Equal("VenueCancelled", snapshot.GetProperty("terminalReason").GetString());
        Assert.Single(
            ledger.GetAlgoMutations("default", ulong.Parse(algoId)),
            mutation => mutation.AlgoOriginIdentity?.ActionKind
                == B3.Trading.Application.Outbound.AlgoOutboundActionKind.Repeg
                && mutation.State
                    == B3.Trading.Application.Outbound.OutboundMutationState.ProvenUnsent);
    }

    [Fact]
    public async Task Pegged_RecoveryWithUnresolvedOutbound_KeepsReconcileAndSchedulingGated()
    {
        // A restart with an unresolved outbound child must classify the
        // mutation before the algo engine can reconcile or schedule more
        // work. Diagnostic reads remain available, but readiness stays
        // closed until authoritative venue evidence or operator resolution.
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
                var cache = f.Services.GetRequiredService<PegBookTopCache>();
                cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

                var algoIdStr = await PostAlgo(http, token,
                    PeggedBody(total: 100, pegRef: "Mid", offsetTicks: 0,
                        repegMs: 100, tickSize: 0.5m));
                algoIdNum = ulong.Parse(algoIdStr);

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                var child1 = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(3));
                child1ClOrdId = child1.ClOrdId;
                var mock = f.Services.GetRequiredService<MockEntryPointClient>();
                mock.ReplaceFailureInjector = _ =>
                    new InvalidOperationException("simulated ambiguous replace send");
                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                await WaitFor(
                    () => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3),
                    "engine did not emit the ambiguous cancel-replace");
                var ledger = f.Services.GetRequiredService<
                    B3.Trading.Application.Outbound.OutboundMutationLedger>();
                await WaitFor(
                    () => ledger.GetAlgoMutations("default", algoIdNum).Any(m =>
                        m.Kind == B3.Trading.Application.Outbound.OutboundMutationKind.Replace
                        && m.State == B3.Trading.Application.Outbound.OutboundMutationState.Ambiguous),
                    TimeSpan.FromSeconds(3),
                    "replace mutation did not become ambiguous");

                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            using (var f2 = TestAppFactory.WithOverrides(overrides))
            using (var http2 = f2.CreateClient())
            {
                var token = await f2.LoginAsync(http2);
                var recovery = f2.Services.GetRequiredService<
                    B3.Trading.Application.Outbound.IOutboundRecoveryGate>();
                await recovery.WaitUntilClassificationCompleteAsync(
                    CancellationToken.None);
                Assert.False(
                    recovery.IsReady,
                    $"phase={recovery.Phase}; statuses={string.Join(';', recovery.Snapshot())}");
                Assert.Equal(
                    B3.Trading.Application.Outbound.OutboundRecoveryPhase.ReconciliationRequired,
                    recovery.Phase);

                // The unresolved outbound mutation keeps the algo engine
                // behind the cold-start gate, so new scheduling cannot race
                // the operator's venue-evidence decision.
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http2.GetAsync("/ready")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await http2.GetAsync("/live")).StatusCode);

                // Diagnostic reads remain available while business scheduling
                // is held closed.
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

    // #345. The orphan-child / stale-cum root cause was the
    // AlgoEngine.OnChildErAsync adoption-before-bookkeeping race fixed
    // in #469 (LiveChildClOrdId reorder + lock fence). The test is fully
    // poll-based (WaitForAlgoStatus / WaitFor), so no retry is needed.
    [Fact]
    public async Task Pegged_FillRacesRepegReplace_DelayInjectedFill_NoOrphanReplacementChildAndIntentReleased()
    {
        // #300 retrofit. Window-3 race for the cancel-replace path:
        // the engine's CancelReplaceAsync wire-call is held by the
        // ReplaceDelayInjector, a Fill ER for the OLD child is
        // injected while the replace is in flight, the gate is
        // released, and we verify:
        //
        //   * Parent transitions to Completed (Fill consumed total).
        //   * ResolveRepegOnFillAsync emits
        //     AlgoPeggedRepegResolvedEvent so the audit pair is
        //     balanced and the PeggedRepegBook entry is cleared.
        //   * ResolveRepegOnFillAsync's TryConsumeByOriginal release
        //     also drops the in-flight PendingReplacementRegistry
        //     intent + any held margin reservation. A late Replaced
        //     ER would therefore be silently dropped (no orphan
        //     replacement child in the book).
        //
        // **Guard pinned**: removing the IsCancelledChild check in
        // the Filled case (AlgoEngine OnChildErAsync) causes the
        // Fill ER to fall through to SubmitNextSliceAsync — a SECOND
        // child would get submitted and Assert.Single(allChildren)
        // below fails.
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
        var registry = f.Services.GetRequiredService<PendingReplacementRegistry>();

        // Hold the engine's CancelReplaceAsync await via a TCS so we
        // have a deterministic window to race the Fill ER against.
        var replaceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mock.ReplaceDelayInjector = _ => replaceGate.Task;

        // Drift the mid → engine issues cancel-replace; the wire-
        // call now parks on the gate.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not invoke CancelReplaceAsync after the mid drift");

        // While the replace is held, inject the racing Fill ER for
        // the OLD child. The ExecutionReportProcessor runs
        // synchronously on the caller thread; the ChildExecutionObservedSignal
        // sits in the channel because the engine consumer is parked
        // on CancelReplaceAsync.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 100);

        // Release CancelReplaceAsync → engine resumes, persists
        // Started (in-memory rt.RepegPending is still true), then
        // dequeues the Fill ER on the next loop iteration and routes
        // through the IsCancelledChild dedup → ResolveRepegOnFillAsync
        // which also consumes the in-flight replace intent.
        replaceGate.SetResult();

        await WaitForAlgoStatus(http, token, algoId, "Completed");
        var snap = await GetAlgo(http, token, algoId);
        Assert.Equal(100, snap.GetProperty("filledQuantity").GetInt64());
        Assert.Equal("None", snap.GetProperty("terminalReason").GetString());

        // AlgoPeggedRepegResolvedEvent dispatched → book cleared.
        await WaitFor(() => repegBook.TryGet("default", ulong.Parse(algoId)) is null,
            TimeSpan.FromSeconds(3),
            "PeggedRepegBook still has an entry after the fill race resolved");

        // ResolveRepegOnFillAsync's TryConsumeByOriginal must have
        // released the in-flight intent — IsOriginalInFlight false.
        Assert.False(registry.IsOriginalInFlight(child1.ClOrdId),
            "in-flight replace intent for the OLD child must be released after fill-race resolution");

        // Exactly one cancel-replace, no replacement child spawned
        // from the Fill handler.
        Assert.Single(mock.SubmittedReplaces);
        Assert.Empty(mock.SubmittedCancels);
        var allChildren = book.EnumerateChildrenOf("default", ulong.Parse(algoId)).ToList();
        Assert.Single(allChildren);
        Assert.Equal(child1.ClOrdId, allChildren[0].ClOrdId);
    }

    [Fact]
    public async Task Pegged_FillRacesRepegReplace_DocumentsObservableInvariant()
    {
        // #300 retrofit + Pass-4 review (#296) P1 spirit. Windows
        // 1 + 2 (Fill processed before/right after CancelReplaceAsync
        // is invoked) are NOT naturally reachable in the current
        // single-consumer reactor under cancel-replace either: the
        // engine awaits CancelReplaceAsync inside its own consumer
        // task, so a ChildExecutionObservedSignal racing the replace
        // can only be dequeued AFTER CancelReplaceAsync returns AND
        // the post-replace Started dispatch runs in the same
        // iteration (which is always Window 3).
        //
        // This test pins the OBSERVABLE invariant that holds across
        // every window: regardless of which sub-window the race
        // resolves in, the engine ends with (a) the parent in a
        // coherent terminal state, (b) no orphan replacement child,
        // (c) no lingering Started without a matching Resolved, and
        // (d) no orphan PendingReplacementRegistry entry.
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
        var registry = f.Services.GetRequiredService<PendingReplacementRegistry>();

        // Drift → engine issues cancel-replace for child1.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit the cancel-replace");

        // Race the Fill ER in immediately.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 100);
        await WaitForAlgoStatus(http, token, algoId, "Completed");

        await WaitFor(() => repegBook.TryGet("default", ulong.Parse(algoId)) is null,
            TimeSpan.FromSeconds(3),
            "PeggedRepegBook still has an entry after the fill race");

        Assert.Single(mock.SubmittedReplaces);
        Assert.Empty(mock.SubmittedCancels);
        Assert.False(registry.IsOriginalInFlight(child1.ClOrdId),
            "replace intent for OLD child must be released after fill-race resolution");
        var allChildren = book.EnumerateChildrenOf("default", ulong.Parse(algoId)).ToList();
        Assert.Single(allChildren);
    }

    [Fact]
    public async Task Pegged_LateCancelOnOldChildAfterFillResolution_IsNoOpViaDedupRing()
    {
        // #300 retrofit. After the Fill-before-replace-ack race
        // resolves (Resolved dispatched, book cleared,
        // RepegPending=false, parent Completed), a spurious venue
        // Cancelled ER may still arrive for the same OLD child (rare
        // FIX gateways emit both Cancelled and Replaced; or a pre-
        // #300 WAL replay still emits Cancelled). That ER MUST be a
        // no-op via the cancelled-child dedup ring — NOT a second
        // SubmitNextSliceAsync call (which would duplicate the
        // replacement child).
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
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit the cancel-replace");
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

        // Cycle A: drift → cancel-replace child1, then inject the
        // Replaced ER so child2 lands.
        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit cancel-replace A");
        var replaceA = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1.ClOrdId);
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replaceA.NewClOrdId,
            origClOrdId: replaceA.OriginalClOrdId,
            leavesQuantity: replaceA.NewQuantity);
        var child2 = await WaitForChildOtherThan(book, algoId, child1.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(replaceA.NewClOrdId, child2.ClOrdId);

        // Cycle B: drift → cancel-replace child2. Do NOT ack —
        // leaves RepegPending=true and the single-slot marker
        // pointing at child2 (so child1 is no longer "the" sticky
        // cancelled id).
        cache.UpdateBookTop("PETR4", 31.5m, 32.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child2.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not emit cancel-replace B");

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

                // Cycle A: cancel-replace + ack → child2 placed.
                // child1 ends up in the history ring; the
                // PeggedRepegBook pending entry is cleared by the
                // Resolved event.
                cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
                await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit cancel-replace A pre-restart");
                var replaceA = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1ClOrdId);
                await InjectReplacedEr(http, adminToken,
                    newClOrdId: replaceA.NewClOrdId,
                    origClOrdId: replaceA.OriginalClOrdId,
                    leavesQuantity: replaceA.NewQuantity);
                var child2 = await WaitForChildOtherThan(book, algoIdStr, child1ClOrdId,
                    TimeSpan.FromSeconds(3));
                child2ClOrdId = child2.ClOrdId;

                // Cycle B: cancel-replace child2, no ack. The
                // pending entry now references child2; child1 lives
                // only in the history ring.
                cache.UpdateBookTop("PETR4", 31.5m, 32.5m, DateTimeOffset.UtcNow);
                await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child2ClOrdId),
                    TimeSpan.FromSeconds(3), "engine did not emit cancel-replace B pre-restart");

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

    // ──────────────── #300 retrofit regression tests ────────────────

    [Fact]
    public async Task Pegged_RepegUsesReplaceNotCancel()
    {
        // #300 retrofit. Belt-and-suspenders: explicitly assert that
        // the Pegged repeg cycle issues a cancel-replace (preserving
        // venue time priority) and NOT a bare cancel followed by a
        // brand-new order. The other migrated tests check this
        // indirectly via SubmittedReplaces / Empty(SubmittedCancels);
        // this one keeps a dedicated, unambiguous regression anchor.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(total: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedNewOrders.Any(o => o.ClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3),
            "initial child was not dispatched through the outbound coordinator");
        var newOrdersBefore = mock.SubmittedNewOrders.Count;

        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3),
            "engine never issued a CancelReplace for the live child");

        // Drive the cycle to completion so we can definitively assert
        // no NewOrderSingle was sent on the repeg path.
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1.ClOrdId);
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);
        _ = await WaitForChildOtherThan(book, algoId, child1.ClOrdId,
            TimeSpan.FromSeconds(3));

        // No bare cancel was issued by the repeg path …
        Assert.DoesNotContain(mock.SubmittedCancels, c => c.OrigClOrdId == child1.ClOrdId);
        // … and no NewOrderSingle either (the replacement child
        // hydrates from the Replaced ER, not from a fresh submit).
        Assert.Equal(newOrdersBefore, mock.SubmittedNewOrders.Count);
    }

    [Fact]
    public async Task Pegged_FillOnOldChildDuringRepegReplace_NoDoubleCount()
    {
        // #300 retrofit. Operator-side analog of
        // <c>AlgoModifyEndpointTests.Modify_FillOnOldChildBeforeReplacedEr_NoDoubleCounting</c>:
        // a Fill ER for the OLD child arrives while the cancel-
        // replace is in flight; a late Replaced ER for the same
        // cycle MUST be silently dropped (the intent was consumed
        // by ResolveRepegOnFillAsync) so the parent's filled
        // quantity does NOT include phantom qty from a hydrated
        // replacement child.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        // Pegged emits a single working slice that covers the full
        // total, so to drive the OLD child to terminal Filled (which
        // is what routes through ResolveRepegOnFillAsync) we must
        // fill the full quantity. The parent will also reach
        // Completed — the "no double-count" assertion is that
        // FilledQuantity == TotalQuantity (exactly one fill credited)
        // even though a late Replaced ER will arrive after settlement.
        var algoId = await PostAlgo(http, token, PeggedBody(total: 200, repegMs: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        var registry = f.Services.GetRequiredService<PendingReplacementRegistry>();

        // Hold the replace so we can race the Fill ER deterministically.
        var replaceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mock.ReplaceDelayInjector = _ => replaceGate.Task;

        cache.UpdateBookTop("PETR4", 30.5m, 31.5m, DateTimeOffset.UtcNow);
        await WaitFor(() => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == child1.ClOrdId),
            TimeSpan.FromSeconds(3), "engine did not invoke CancelReplaceAsync");

        // Inject the racing Fill on the OLD child while the replace
        // is held. Fill the full child quantity to drive the OLD
        // child to terminal Filled (Pegged child qty == TotalQuantity).
        // Then release the gate and let the engine drain.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 200);
        replaceGate.SetResult();

        // Wait until ResolveRepegOnFillAsync has consumed the intent.
        await WaitFor(() => !registry.IsOriginalInFlight(child1.ClOrdId),
            TimeSpan.FromSeconds(3),
            "in-flight replace intent for OLD child was not released after fill-race resolution");

        // Now inject the late Replaced ER. The processor's
        // PendingReplacementRegistry intercept misses (we already
        // consumed it) — the ER becomes a phantom replacement that
        // must be dropped without altering parent state.
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == child1.ClOrdId);
        var lateReplacedReq = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(new
            {
                ClOrdId = replace.NewClOrdId,
                Type = "Replaced",
                LastQty = replace.NewQuantity,
                OrigClOrdId = replace.OriginalClOrdId,
            }),
        };
        lateReplacedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        // Late ER may be rejected with 404 (order already terminal /
        // intent already consumed) or accepted-and-dropped depending
        // on processor pathways; both outcomes are acceptable — what
        // we care about is the FilledQuantity assertion below.
        await http.SendAsync(lateReplacedReq);
        await Task.Delay(200);

        var snap = await GetAlgo(http, token, algoId);
        Assert.Equal(200, snap.GetProperty("filledQuantity").GetInt64());
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

    /// <summary>
    /// #300 retrofit. Pegged repeg now uses cancel-replace. Mirror of
    /// <c>AlgoModifyEndpointTests.InjectReplacedEr</c>: posts a
    /// Replaced-type simulator ER for the replacement <paramref name="newClOrdId"/>
    /// with <paramref name="origClOrdId"/> echoed so the processor
    /// hydrates the new child into the WorkingOrderBook and re-emits
    /// <c>ChildExecutionObservedSignal</c>. <paramref name="leavesQuantity"/>
    /// is repurposed as the LastQty hint for the Replaced injector arm
    /// (no fill on a replace ack).
    /// </summary>
    private static async Task<HttpResponseMessage> InjectReplacedEr(
        HttpClient http, string adminToken, ulong newClOrdId, ulong origClOrdId, long leavesQuantity,
        long? cumQty = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(new
            {
                ClOrdId = newClOrdId,
                Type = "Replaced",
                LastQty = leavesQuantity,
                OrigClOrdId = origClOrdId,
                CumQty = cumQty,
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

    private sealed class ProvenUnsentCancelGateway : IExchangeGateway
    {
        private readonly EntryPointClientGateway _inner;
        private int _provenUnsentReplaceFailuresRemaining = int.MaxValue;

        public ProvenUnsentCancelGateway(EntryPointClientGateway inner) =>
            _inner = inner;

        public System.Collections.Concurrent.ConcurrentQueue<ulong>
            AttemptedCancelClOrdIds
        { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<ulong>
            AttemptedReplaceClOrdIds
        { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<decimal?>
            AttemptedReplacePrices
        { get; } = new();
        public int ProvenUnsentReplaceFailuresRemaining
        {
            get => Volatile.Read(ref _provenUnsentReplaceFailuresRemaining);
            set => Volatile.Write(ref _provenUnsentReplaceFailuresRemaining, value);
        }

        public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
            _inner.SubmitAsync(order, cancellationToken);

        public Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            Order order,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken) =>
            _inner.SubmitWithReceiptAsync(order, onFramePrepared, cancellationToken);

        public Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            B3.Trading.Application.Outbound.OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken) =>
            _inner.SubmitWithReceiptAsync(command, onFramePrepared, cancellationToken);

        public Task CancelAsync(
            Order order,
            ulong newClOrdId,
            CancellationToken cancellationToken) =>
            _inner.CancelAsync(order, newClOrdId, cancellationToken);

        public Task<ExchangeGatewayReceipt> CancelWithReceiptAsync(
            B3.Trading.Application.Outbound.OutboundCancelCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken)
        {
            AttemptedCancelClOrdIds.Enqueue(
                command.Canonical.ClOrdId);
            return Task.FromException<ExchangeGatewayReceipt>(
                new ExchangeGatewayAttemptException(
                    "typed pre-frame cancel failure",
                    ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
                    ExchangeGatewayAttemptStage.NotStarted,
                    frame: null));
        }

        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken) =>
            _inner.CancelReplaceAsync(
                original,
                newClOrdId,
                newQuantity,
                newPrice,
                requestedTimeInForce,
                requestedStopPrice,
                requestedGoodTillDate,
                cancellationToken);

        public Task<ExchangeGatewayReceipt> CancelReplaceWithReceiptAsync(
            B3.Trading.Application.Outbound.OutboundReplaceCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken)
        {
            AttemptedReplaceClOrdIds.Enqueue(command.Canonical.ClOrdId);
            AttemptedReplacePrices.Enqueue(command.Canonical.Price);
            if (Interlocked.Decrement(ref _provenUnsentReplaceFailuresRemaining) >= 0)
            {
                return Task.FromException<ExchangeGatewayReceipt>(
                    new ExchangeGatewayAttemptException(
                        "typed pre-frame replace failure",
                        ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
                        ExchangeGatewayAttemptStage.NotStarted,
                        frame: null));
            }
            return _inner.CancelReplaceWithReceiptAsync(
                command,
                onFramePrepared,
                cancellationToken);
        }
    }
}
