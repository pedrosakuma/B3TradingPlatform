using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for the POV algo (Q3.2 / #282). Mirrors the shape
/// of <see cref="VwapAlgoEndpointTests"/>: real wall-clock with tightly
/// bounded sub-second windows so the scheduler ticks and the engine
/// reactor exercise the full POV path through the simulator.
/// </summary>
public class PovAlgoEndpointTests
{
    private static IDictionary<string, string?> Simulator() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
            ["Trading:Exchange:AllowErInjection"] = "true",
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
        };

    private static object PovBody(long total, DateTimeOffset start, DateTimeOffset end,
        double tickSeconds = 0.2, string childType = "Limit", decimal? childPrice = 30m,
        decimal participationRate = 0.20m, decimal? priceLimit = null, long? minSliceQty = null) => new
        {
            Symbol = "PETR4",
            Side = "Buy",
            Type = "Pov",
            TotalQuantity = total,
            Pov = new
            {
                StartUtc = start,
                EndUtc = end,
                ChildOrderType = childType,
                ChildPrice = childPrice,
                ParticipationRate = participationRate,
                TickIntervalSeconds = tickSeconds,
                PriceLimit = priceLimit,
                MinSliceQty = minSliceQty,
            },
        };

    // ───────────────────────── POST validation ─────────────────────────

    [Fact]
    public async Task PostAlgo_PovWithoutParams_Returns400()
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
                Type = "Pov",
                TotalQuantity = 100,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_PovEndBeforeStart_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PovBody(100, now.AddMinutes(5), now.AddMinutes(1))),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_PovRateOutOfRange_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PovBody(100, now, now.AddMinutes(1), participationRate: 1.5m)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var req2 = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PovBody(100, now, now.AddMinutes(1), participationRate: 0m)),
        };
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp2 = await http.SendAsync(req2);
        Assert.Equal(HttpStatusCode.BadRequest, resp2.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_PovLimitWithoutPrice_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PovBody(100, now, now.AddMinutes(1), childPrice: null)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_PovInvalidMinSliceQty_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(PovBody(100, now, now.AddMinutes(1), minSliceQty: 0)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ───────────────────────── Happy-path slicing ─────────────────────────

    [Fact]
    public async Task Pov_SlicesAfterMarketTrades()
    {
        // No market volume seen → POV must NOT slice (cumMarketVolume = 0).
        // Once an admin trade-print injects volume, the next tick yields a
        // slice we can fill. Window 2s, tick=200ms, rate=20%, total=400.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            PovBody(total: 400, start: now.AddSeconds(-1), end: now.AddSeconds(3),
                tickSeconds: 0.2, participationRate: 0.20m));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();

        // Pump a trade volume directly via the volume curve estimator
        // to drive the next slice. Pass-1 review (#295) P1#2 made
        // VolumeBetween pro-rate boundary buckets by elapsed-time
        // fraction, so a single qty must be large enough that the
        // POV's sub-bucket integration window (200ms of a 5-min
        // bucket = 1/1500) still leaves meaningful share. 1_500_000 →
        // 200ms-pro-rated ≈ 1000 → 20% participation slice = 200.
        var curve = f.Services.GetRequiredService<B3.Trading.Application.MarketData.VolumeCurveEstimator>();
        curve.RecordTrade("PETR4", 1_500_000, now.AddSeconds(-1).AddMilliseconds(50));

        var first = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        // Pro-rated bucket fraction → ~1000 cum mv → 20% → 200; clamped by remaining (400) → 200.
        Assert.True(first.Quantity > 0);
        await WaitForNewOrderDispatch(
            f.Services.GetRequiredService<MockEntryPointClient>(),
            first.ClOrdId);
        await InjectEr(http, adminToken, first.ClOrdId, "Fill", lastQty: first.Quantity);

        // Push more volume so a second slice can fire and the parent
        // completes (or expires) within the window. Same 1500x scaling
        // applies (sub-bucket window).
        curve.RecordTrade("PETR4", 3_000_000, now.AddSeconds(-1).AddMilliseconds(200));

        var seenSeqs = new HashSet<int> { first.AlgoSliceSeq!.Value };
        long filled = first.Quantity;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            var snap = await GetAlgo(http, userToken, algoId);
            var status = snap.GetProperty("status").GetString();
            if (status is "Completed" or "Expired") break;

            var next = book.EnumerateChildrenOf("default", ulong.Parse(algoId))
                .FirstOrDefault(c => c.AlgoSliceSeq is { } s && seenSeqs.Add(s));
            if (next is null) { await Task.Delay(20); continue; }
            await WaitForNewOrderDispatch(
                f.Services.GetRequiredService<MockEntryPointClient>(),
                next.ClOrdId);
            await InjectEr(http, adminToken, next.ClOrdId, "Fill", lastQty: next.Quantity);
            filled += next.Quantity;
            if (filled >= 400) break;
        }

        await WaitForAlgoStatus(http, userToken, algoId, "Completed", "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        var filledQty = algo.GetProperty("filledQuantity").GetInt64();
        Assert.True(filledQty > 0, $"expected some POV fills, got {filledQty}");
    }

    [Fact]
    public async Task Pov_WindowExpiresWithoutVolume_BecomesExpired()
    {
        // No market prints injected during the entire window — POV is
        // opportunistic and must NOT force any child. After endUtc the
        // parent transitions to Expired/PovWindowExpired.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            PovBody(total: 100, start: now.AddSeconds(-1), end: now.AddMilliseconds(500),
                tickSeconds: 0.1));

        await WaitForAlgoStatus(http, userToken, algoId, "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("PovWindowExpired", algo.GetProperty("terminalReason").GetString());
        Assert.Equal(0, algo.GetProperty("filledQuantity").GetInt64());
    }

    [Fact]
    public async Task Pov_NoChild_WindowAlreadyExpired_BecomesExpired()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            PovBody(total: 100, start: now.AddSeconds(-2), end: now.AddSeconds(-1),
                tickSeconds: 0.2));

        await WaitForAlgoStatus(http, userToken, algoId, "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("PovWindowExpired", algo.GetProperty("terminalReason").GetString());
        Assert.Equal(0, algo.GetProperty("filledQuantity").GetInt64());
    }

    [Fact]
    public async Task GetAlgo_ReturnsPovParametersInDto()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, token,
            PovBody(total: 200, start: now.AddSeconds(10), end: now.AddSeconds(70),
                tickSeconds: 5, participationRate: 0.15m, priceLimit: 31m, minSliceQty: 10));

        var algo = await GetAlgo(http, token, algoId);
        Assert.Equal("Pov", algo.GetProperty("type").GetString());
        var pov = algo.GetProperty("pov");
        Assert.Equal(5.0, pov.GetProperty("tickIntervalSeconds").GetDouble(), 3);
        Assert.Equal(0.15m, pov.GetProperty("participationRate").GetDecimal());
        Assert.Equal(31m, pov.GetProperty("priceLimit").GetDecimal());
        Assert.Equal(10L, pov.GetProperty("minSliceQty").GetInt64());
    }

    // ───────────────────── Cancel-mid-flight ─────────────────────

    /// <summary>
    /// Recording <see cref="IAlgoEventSink"/> reused from the VWAP
    /// equivalent to assert exactly-once terminal emission for POV.
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
    public async Task Pov_CancelMidFlight_CancelsLiveChildAndTerminalEmittedOnce()
    {
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

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            PovBody(total: 400, start: now.AddSeconds(-1), end: now.AddSeconds(10),
                tickSeconds: 0.2, participationRate: 0.20m));

        // Drive the engine to slice by injecting market volume. Pass-1
        // review (#295) P1#2 — see Pov_SlicesAfterMarketTrades for the
        // pro-rate sizing rationale.
        var curve = f.Services.GetRequiredService<B3.Trading.Application.MarketData.VolumeCurveEstimator>();
        curve.RecordTrade("PETR4", 1_500_000, now.AddSeconds(-1).AddMilliseconds(50));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var inFlight = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var del = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, del.StatusCode);

        await InjectEr(http, adminToken, inFlight.ClOrdId, "Canceled");

        await WaitForAlgoStatus(http, userToken, algoId, "Cancelled");
        var snap1 = await GetAlgo(http, userToken, algoId);
        Assert.Equal("UserCancelled", snap1.GetProperty("terminalReason").GetString());
        var terminalAt1 = snap1.GetProperty("terminalAtUtc").GetDateTimeOffset();

        var req2 = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoId}");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var del2 = await http.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Conflict, del2.StatusCode);

        await Task.Delay(50);
        var snap2 = await GetAlgo(http, userToken, algoId);
        Assert.Equal("Cancelled", snap2.GetProperty("status").GetString());
        Assert.Equal(terminalAt1, snap2.GetProperty("terminalAtUtc").GetDateTimeOffset());

        Assert.Equal(1, sink!.TerminalPublishCount(ulong.Parse(algoId)));
    }

    // ──────────── PovProgressBook persistence regressions (#295 pass-2) ────────────

    // These three tests replace two earlier AlgoRecoveryTests cases that
    // stubbed PovProgressBook directly (manual Set / Remove). The stubbed
    // versions would still pass if AlgoEngine.ComputePovSlice stopped
    // writing povProgress on the skip path, or if RecordTerminalAsync
    // stopped pruning on terminal — both regressions the reviewer flagged
    // as P2 because the test asserts on a side-effect that the test itself
    // produced. The replacements below boot the production host, drive
    // the real engine through the API + simulator, and only then assert
    // on PovProgressBook state and snapshot/restart durability.

    private static IDictionary<string, string?> PersistenceOverrides(string dataDir)
    {
        var d = new Dictionary<string, string?>(Simulator())
        {
            ["Trading:Persistence:Enabled"] = "true",
            ["Trading:Persistence:DataDirectory"] = dataDir,
            ["Trading:Persistence:FirmId"] = "default",
            // Long interval so the periodic snap doesn't race the test's
            // own explicit TryTakeSnapshot. The test forces the snapshot
            // boundary it cares about.
            ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
        };
        return d;
    }

    private static B3.Trading.Infrastructure.Persistence.SnapshotService ResolveSnapshotService(TestAppFactory f) =>
        f.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<B3.Trading.Infrastructure.Persistence.SnapshotService>()
            .Single();

    [Fact]
    public async Task Pov_SkipTickProgress_PersistedAcrossSnapshotRestart()
    {
        // Drive the real engine: MinSliceQty > integrated cum-mv * rate so
        // ComputePovSlice returns 0 (skip). The engine still must call
        // _povProgress.Set on every tick — otherwise a restart between
        // snapshots loses the observed market volume. The original stubbed
        // test set povProgress by hand; this version exercises the engine.
        var dataDir = Path.Combine(Path.GetTempPath(), "b3-pov-skip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;
            long observedMv = 0;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var token = await f.LoginAsync(http);
                var now = DateTimeOffset.UtcNow;

                // 200ms-pro-rated bucket of 1.5M ≈ 1000 cum-mv → 20% rate
                // → 200 target. MinSliceQty=1_000_000 forces qty=0 on every
                // tick, so no child is ever submitted — pure skip path.
                var algoIdStr = await PostAlgo(http, token,
                    PovBody(total: 100, start: now.AddSeconds(-1), end: now.AddSeconds(8),
                        tickSeconds: 0.2, participationRate: 0.20m, minSliceQty: 1_000_000));
                algoIdNum = ulong.Parse(algoIdStr);

                var curve = f.Services.GetRequiredService<B3.Trading.Application.MarketData.VolumeCurveEstimator>();
                curve.RecordTrade("PETR4", 1_500_000, now.AddSeconds(-1).AddMilliseconds(50));

                var povBook = f.Services.GetRequiredService<PovProgressBook>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                PovProgress? entry = null;
                while (sw.Elapsed < TimeSpan.FromSeconds(5))
                {
                    entry = povBook.TryGet("default", algoIdNum);
                    if (entry is { MarketVolumeSeen: > 0 }) break;
                    await Task.Delay(20);
                }
                Assert.NotNull(entry);
                Assert.True(entry!.Value.MarketVolumeSeen > 0,
                    $"engine did not advance PovProgressBook on skip tick (mv={entry.Value.MarketVolumeSeen})");
                observedMv = entry.Value.MarketVolumeSeen;

                // Skip path: no child should have been emitted.
                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                Assert.Empty(book.EnumerateChildrenOf("default", algoIdNum));

                // Force the snapshot boundary explicitly.
                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            // Cold restart with the same data dir: recovery loads the
            // snapshot and PovProgressBook re-hydrates with the skip-tick
            // baseline.
            using (var f2 = TestAppFactory.WithOverrides(overrides))
            {
                using var http = f2.CreateClient();
                await f2.Services
                    .GetRequiredService<IOutboundRecoveryGate>()
                    .WaitUntilClassificationCompleteAsync(CancellationToken.None);
                var povBook2 = f2.Services.GetRequiredService<PovProgressBook>();
                var restored = povBook2.TryGet("default", algoIdNum);
                Assert.NotNull(restored);
                Assert.True(restored!.Value.MarketVolumeSeen >= observedMv);
            }
        }
        finally
        {
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Pov_CancelledTerminal_PrunesPovProgress_AndStaysPrunedAcrossRestart()
    {
        // Drive the real engine to terminal/Cancelled and verify
        // RecordTerminalAsync pruned the PovProgressBook entry. Restart
        // must observe the same: snapshot taken AFTER the prune carries
        // no row for the dead algo.
        var dataDir = Path.Combine(Path.GetTempPath(), "b3-pov-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var userToken = await f.LoginAsync(http);
                var adminToken = await f.LoginAsync(http, "admin");
                var now = DateTimeOffset.UtcNow;

                var algoIdStr = await PostAlgo(http, userToken,
                    PovBody(total: 400, start: now.AddSeconds(-1), end: now.AddSeconds(30),
                        tickSeconds: 0.2, participationRate: 0.20m));
                algoIdNum = ulong.Parse(algoIdStr);

                var curve = f.Services.GetRequiredService<B3.Trading.Application.MarketData.VolumeCurveEstimator>();
                curve.RecordTrade("PETR4", 1_500_000, now.AddSeconds(-1).AddMilliseconds(50));

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                var inFlight = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(5));
                await WaitForNewOrderDispatch(
                    f.Services.GetRequiredService<MockEntryPointClient>(),
                    inFlight.ClOrdId);

                // Pre-cancel sanity: the engine populated PovProgressBook
                // for the slice that fired.
                var povBook = f.Services.GetRequiredService<PovProgressBook>();
                Assert.NotNull(povBook.TryGet("default", algoIdNum));

                var del = new HttpRequestMessage(HttpMethod.Delete, $"/algo/{algoIdStr}");
                del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
                var delResp = await http.SendAsync(del);
                Assert.Equal(HttpStatusCode.Accepted, delResp.StatusCode);

                await InjectEr(http, adminToken, inFlight.ClOrdId, "Canceled");
                await WaitForAlgoStatus(http, userToken, algoIdStr, "Cancelled");

                // RecordTerminalAsync runs after the dispatcher round-trip;
                // poll briefly for the prune to be observable.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (povBook.TryGet("default", algoIdNum) is not null && sw.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(20);
                Assert.Null(povBook.TryGet("default", algoIdNum));

                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            using (var f2 = TestAppFactory.WithOverrides(overrides))
            {
                _ = f2.CreateClient();
                var povBook2 = f2.Services.GetRequiredService<PovProgressBook>();
                Assert.Null(povBook2.TryGet("default", algoIdNum));
            }
        }
        finally
        {
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Pov_CompletedTerminal_PrunesPovProgress_AndStaysPrunedAcrossRestart()
    {
        // Sibling of the Cancelled variant: drive the engine to
        // terminal/Completed (filledQty == totalQty) and verify the same
        // prune+snapshot+restart invariant. Reviewer asked for both
        // terminal kinds.
        var dataDir = Path.Combine(Path.GetTempPath(), "b3-pov-complete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var overrides = PersistenceOverrides(dataDir);
            ulong algoIdNum = 0;

            using (var f = TestAppFactory.WithOverrides(overrides))
            using (var http = f.CreateClient())
            {
                var userToken = await f.LoginAsync(http);
                var adminToken = await f.LoginAsync(http, "admin");
                var now = DateTimeOffset.UtcNow;

                // Tiny total so a single slice can fill the parent —
                // 200ms-pro-rated bucket of 1.5M ≈ 1000 cum-mv → 20% →
                // 200 target, clamped by remaining (100) → 100. Single
                // Fill ER takes the parent to Completed.
                var algoIdStr = await PostAlgo(http, userToken,
                    PovBody(total: 100, start: now.AddSeconds(-1), end: now.AddSeconds(30),
                        tickSeconds: 0.2, participationRate: 0.20m));
                algoIdNum = ulong.Parse(algoIdStr);

                var curve = f.Services.GetRequiredService<B3.Trading.Application.MarketData.VolumeCurveEstimator>();
                curve.RecordTrade("PETR4", 1_500_000, now.AddSeconds(-1).AddMilliseconds(50));

                var book = f.Services.GetRequiredService<WorkingOrderBook>();
                var child = await WaitForAnyChild(book, algoIdStr, TimeSpan.FromSeconds(5));
                await WaitForNewOrderDispatch(
                    f.Services.GetRequiredService<MockEntryPointClient>(),
                    child.ClOrdId);

                var povBook = f.Services.GetRequiredService<PovProgressBook>();
                Assert.NotNull(povBook.TryGet("default", algoIdNum));

                await InjectEr(http, adminToken, child.ClOrdId, "Fill", lastQty: child.Quantity);
                await WaitForAlgoStatus(http, userToken, algoIdStr, "Completed");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (povBook.TryGet("default", algoIdNum) is not null && sw.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(20);
                Assert.Null(povBook.TryGet("default", algoIdNum));

                ResolveSnapshotService(f).TryTakeSnapshot();
            }

            using (var f2 = TestAppFactory.WithOverrides(overrides))
            {
                _ = f2.CreateClient();
                var povBook2 = f2.Services.GetRequiredService<PovProgressBook>();
                Assert.Null(povBook2.TryGet("default", algoIdNum));
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

    private static async Task WaitForNewOrderDispatch(
        MockEntryPointClient client,
        ulong clOrdId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (client.SubmittedNewOrders.Any(order => order.ClOrdId == clOrdId))
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"New order {clOrdId} was not dispatched within 3s.");
    }

    private static async Task WaitForAlgoStatus(HttpClient http, string token, string algoId, params string[] anyOf)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? last = null;
        string? lastReason = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var algo = await GetAlgo(http, token, algoId);
            last = algo.GetProperty("status").GetString();
            lastReason = algo.GetProperty("terminalReason").GetString();
            if (anyOf.Contains(last)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(
            $"Algo {algoId} did not reach any of [{string.Join(",", anyOf)}] within 5s; last={last}/{lastReason}");
    }
}
