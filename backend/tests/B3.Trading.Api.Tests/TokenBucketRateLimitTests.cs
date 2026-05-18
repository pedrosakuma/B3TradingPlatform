using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.RateLimit;
using B3.Trading.Application;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.4 (#304). End-to-end + unit coverage for the per-user × endpoint
/// token-bucket rate limiter. Every HTTP-flavoured test opts in to the
/// limiter via <c>WithOverrides</c> — the test factory defaults it OFF
/// so the rest of the suite (which hammers /orders / /algo) is
/// unaffected.
/// </summary>
public class TokenBucketRateLimitTests
{
    /// <summary>
    /// Shared overrides for the HTTP integration tests: enable the
    /// limiter and pin the /orders POST rule to burst=5 with a glacial
    /// refill so the assertions are not racing the token-bucket
    /// regeneration on a busy CI host. The default refill (5/s) plus
    /// HTTP round-trip latency under parallel test execution would
    /// otherwise top the bucket back up between calls.
    /// </summary>
    private static Dictionary<string, string?> EnableLimiterDeterministic() => new()
    {
        ["Trading:RateLimit:Enabled"] = "true",
        ["Trading:RateLimit:Rules:0:PathPattern"] = "/orders",
        ["Trading:RateLimit:Rules:0:Methods:0"] = "POST",
        ["Trading:RateLimit:Rules:0:Methods:1"] = "PUT",
        ["Trading:RateLimit:Rules:0:Methods:2"] = "DELETE",
        ["Trading:RateLimit:Rules:0:Methods:3"] = "PATCH",
        ["Trading:RateLimit:Rules:0:Burst"] = "5",
        ["Trading:RateLimit:Rules:0:RefillPerSecond"] = "0.01",
        ["Trading:RateLimit:Rules:1:PathPattern"] = "/auth/login",
        ["Trading:RateLimit:Rules:1:Burst"] = "3",
        ["Trading:RateLimit:Rules:1:RefillPerSecond"] = "0.01",
    };

    // ---- Pure unit tests over the limiter primitive (no HTTP). ----

    [Fact]
    public void TryAcquire_BurstThenRejects_ReportsRetryAfter()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new TokenBucketRateLimiter(() => clock, startSweeper: false);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("u", "/orders", burst: 5, refillPerSecond: 5, out _));
        }

        var ok = limiter.TryAcquire("u", "/orders", burst: 5, refillPerSecond: 5, out var retry);
        Assert.False(ok);
        // No time elapsed and 0 tokens left → wait 1/5s = 0.2s.
        Assert.InRange(retry, 0.15, 0.25);
    }

    [Fact]
    public void TryAcquire_AfterRefillWindow_GrantsMoreTokens()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new TokenBucketRateLimiter(() => clock, startSweeper: false);

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire("u", "/orders", 5, 5, out _));
        Assert.False(limiter.TryAcquire("u", "/orders", 5, 5, out _));

        // Advance 1.1 s; with refill=5/s that gives 5.5 tokens
        // (capped at burst=5). Five more must succeed.
        clock = clock.AddMilliseconds(1100);
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire("u", "/orders", 5, 5, out _));
        Assert.False(limiter.TryAcquire("u", "/orders", 5, 5, out _));
    }

    [Fact]
    public void TryAcquire_PerUserBucketIsolation()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new TokenBucketRateLimiter(() => clock, startSweeper: false);

        // Exhaust user A.
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire("alice", "/orders", 5, 5, out _));
        Assert.False(limiter.TryAcquire("alice", "/orders", 5, 5, out _));

        // User B is untouched — still has a full bucket.
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire("bob", "/orders", 5, 5, out _));
    }

    [Fact]
    public void TryAcquire_PerEndpointBucketIsolation()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new TokenBucketRateLimiter(() => clock, startSweeper: false);

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire("alice", "/orders", 5, 5, out _));
        Assert.False(limiter.TryAcquire("alice", "/orders", 5, 5, out _));

        // Same user, different endpoint key — independent bucket.
        Assert.True(limiter.TryAcquire("alice", "/positions", 100, 100, out _));
    }

    [Fact]
    public void IdleBuckets_EvictedBySweeper()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new TokenBucketRateLimiter(() => clock, startSweeper: false);

        limiter.TryAcquire("ghost", "/orders", 5, 5, out _);
        Assert.Equal(1, limiter.BucketCount);

        // Advance past the 1-hour idle TTL.
        clock = clock.AddHours(2);
        limiter.SweepIdleBucketsForTest();
        Assert.Equal(0, limiter.BucketCount);
    }

    [Fact]
    public void TryAcquire_RaceWithSweep_DoesNotAdmitDoubleBurst()
    {
        // Reproduces the bug fixed in P2.2 on PR #321: the sweeper
        // could evict a bucket between a request's GetOrAdd and its
        // lock-and-acquire; a concurrent request would then create a
        // fresh full bucket. Without the Evicted-flag retry both
        // requests grant tokens — up to 2× the configured burst.
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new TokenBucketRateLimiter(() => clock, startSweeper: false);

        const int burst = 5;
        const double refill = 5;

        // Prime bucket B1.
        Assert.True(limiter.TryAcquire("u", "/orders", burst, refill, out _));

        // On the NEXT TryAcquire, after GetOrAdd returns the existing
        // bucket but before the lock is taken, simulate the race:
        // advance the clock past IdleTtl, sweep (which sets Evicted
        // and removes B1), and then drain a brand-new replacement
        // bucket B2 from inside the hook. When the outer call finally
        // takes B1's lock, the fix must observe Evicted=true, retry
        // GetOrAdd → B2 (now empty), and DENY.
        var fired = 0;
        limiter.OnBucketAcquiredForTest = () =>
        {
            if (Interlocked.Increment(ref fired) != 1) return;
            clock = clock.AddHours(2);
            limiter.SweepIdleBucketsForTest();
            for (var i = 0; i < burst; i++)
                Assert.True(limiter.TryAcquire("u", "/orders", burst, refill, out _));
        };

        var granted = limiter.TryAcquire("u", "/orders", burst, refill, out _);
        Assert.False(granted);

        // Sanity: total tokens consumed = 1 (prime) + 5 (nested) = 6;
        // the outer attempt that lost the race against the sweep must
        // not push that over 6 (i.e. burst+1 across two generations).
        var nextOk = limiter.TryAcquire("u", "/orders", burst, refill, out _);
        Assert.False(nextOk);
    }

    // ---- HTTP integration tests via WebApplicationFactory. ----

    [Fact]
    public async Task Orders_BurstThenRejects_With429AndRetryAfter()
    {
        await using var factory = TestAppFactory.WithOverrides(EnableLimiterDeterministic());

        var http = await factory.CreateAuthedClientAsync();

        // First 5 POSTs against /orders ride the default burst=5
        // rule and are accepted (Accepted/OK status — never 429).
        for (var i = 0; i < 5; i++)
        {
            var resp = await PostMinimalOrder(http);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
        }

        // The 6th must hit the limiter.
        var rejected = await PostMinimalOrder(http);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limited", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Orders_PerUserIsolation_UserBUnaffectedWhenUserAExhausted()
    {
        await using var factory = TestAppFactory.WithOverrides(EnableLimiterDeterministic());

        var alice = await factory.CreateAuthedClientAsync(user: "alice");
        var bob = await factory.CreateAuthedClientAsync(user: "bob");

        // Drain alice's bucket.
        for (var i = 0; i < 5; i++) await PostMinimalOrder(alice);
        var aliceRejected = await PostMinimalOrder(alice);
        Assert.Equal(HttpStatusCode.TooManyRequests, aliceRejected.StatusCode);

        // Bob still has full burst.
        for (var i = 0; i < 5; i++)
        {
            var resp = await PostMinimalOrder(bob);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Orders_PerEndpointIsolation_PositionsUnaffectedByOrdersExhaustion()
    {
        await using var factory = TestAppFactory.WithOverrides(EnableLimiterDeterministic());

        var http = await factory.CreateAuthedClientAsync();

        for (var i = 0; i < 5; i++) await PostMinimalOrder(http);
        var ordersBlocked = await PostMinimalOrder(http);
        Assert.Equal(HttpStatusCode.TooManyRequests, ordersBlocked.StatusCode);

        // GET /positions rides the generic-read rule (burst=100); it
        // must NOT inherit the /orders write bucket's exhaustion.
        var positions = await http.GetAsync("/positions");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, positions.StatusCode);
    }

    [Fact]
    public async Task Login_PreAuthIpBucket_RejectsAfterBurst()
    {
        await using var factory = TestAppFactory.WithOverrides(EnableLimiterDeterministic());

        // Default /auth/login rule: burst=3, refill=1/s. From the same
        // IP (localhost in the test host) the 4th attempt — within the
        // same second — must be rejected with 429. Use a deliberately
        // bad password so the test isn't sensitive to login success
        // semantics (the limiter trips BEFORE the handler runs).
        using var http = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var resp = await http.PostAsJsonAsync("/auth/login",
                new LoginRequest("does-not-exist", "wrong"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
        }
        var rejected = await http.PostAsJsonAsync("/auth/login",
            new LoginRequest("does-not-exist", "wrong"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Rejection_IncrementsMetric_WithPathAndPrincipalKindTags()
    {
        await using var factory = TestAppFactory.WithOverrides(EnableLimiterDeterministic());

        var observedPath = (string?)null;
        var observedPrincipalKind = (string?)null;
        var userTagSeen = false;
        var total = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "B3.Trading"
                && instr.Name == "trading.ratelimit.rejected_total")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "path" && tag.Value is string p) observedPath = p;
                if (tag.Key == "principal_kind" && tag.Value is string k) observedPrincipalKind = k;
                // P2.1 fix on PR #321: `user` must NOT be a metric tag
                // — under an IP-spray attack the unbounded set of
                // RemoteIpAddress values would explode cardinality.
                if (tag.Key == "user") userTagSeen = true;
            }
            Interlocked.Add(ref total, value);
        });
        listener.Start();

        var http = await factory.CreateAuthedClientAsync();
        for (var i = 0; i < 5; i++) await PostMinimalOrder(http);
        var rejected = await PostMinimalOrder(http);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        Assert.True(total >= 1);
        Assert.Equal("/orders", observedPath);
        Assert.Equal("user", observedPrincipalKind);
        Assert.False(userTagSeen,
            "The `user` tag must be dropped to keep the metric cardinality bounded; "
            + "the throttled identity is logged on the rejection log line instead.");
    }

    [Fact]
    public async Task BypassRoles_AdminSkipsLimiter()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>(EnableLimiterDeterministic())
        {
            ["Trading:RateLimit:BypassRoles:0"] = "admin",
        });

        // Mint an admin JWT directly via the issuer so we don't even
        // burn an /auth/login token to do the test.
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (token, _) = issuer.Issue("admin", "admin");

        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Push well past the 5-token burst — must NEVER 429 with
        // bypass active.
        for (var i = 0; i < 12; i++)
        {
            var resp = await PostMinimalOrder(http);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
        }
    }

    [Fact]
    public async Task MultiFirm_BucketKeyedByUsernameOnly_LimitsAcrossFirms()
    {
        // Documented design choice: the bucket key is the JWT sub
        // alone, NOT (sub, firm). So the same login flooding /orders
        // across FIRM01 and FIRM02 hits the same bucket — i.e. the
        // limiter clamps the user, not the user-in-a-firm.
        await using var factory = TestAppFactory.WithOverrides(EnableLimiterDeterministic());

        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (firm1, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM01");
        var (firm2, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM02");

        using var http1 = factory.CreateClient();
        http1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firm1);
        using var http2 = factory.CreateClient();
        http2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firm2);

        // Pre-register the owner so /orders POST doesn't 404 on
        // EndClient resolution. Both firm tokens share the same
        // sub-claim so a single Register() call covers both.
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        registry.Register(TestAppFactory.TestUser);

        var sawRejection = false;
        // 6 total writes spread across the two firms (3 each); the
        // shared bucket has burst=5 so at least one must 429.
        for (var i = 0; i < 3; i++) await PostMinimalOrder(http1);
        for (var i = 0; i < 3; i++)
        {
            var resp = await PostMinimalOrder(http2);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) sawRejection = true;
        }
        Assert.True(sawRejection,
            "Expected at least one 429 — same username across firms must share a bucket.");
    }

    [Fact]
    public async Task RateLimit_DisabledByDefault_InTestFactory()
    {
        // Regression guard: the test factory MUST default the limiter
        // off so the broad suite (which fires hundreds of writes) is
        // unaffected. Hammer /orders past the burst and assert no 429.
        using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();

        for (var i = 0; i < 20; i++)
        {
            var resp = await PostMinimalOrder(http);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
        }
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> PostMinimalOrder(HttpClient http)
    {
        // Minimal but valid SubmitOrderRequest — matches the shape
        // OrdersIcebergEndpointTests uses. PETR4 reference price is
        // seeded in the test factory so the collar check is happy.
        var payload = new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 10,
            Price = 30.0m,
        };
        return http.PostAsJsonAsync("/orders", payload);
    }
}
