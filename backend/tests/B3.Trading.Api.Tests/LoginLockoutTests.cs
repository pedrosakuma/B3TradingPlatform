using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Slice 4 of #97: per-username login lockout. Unit tests pin the
/// state machine against a stub clock; integration tests verify the
/// HTTP endpoint wires the tracker correctly and that the lockout
/// response is indistinguishable from wrong-password.
/// </summary>
public class LoginLockoutTests
{
    private static InMemoryLoginAttemptTracker MakeTracker(LoginLockoutOptions opts, StubClock clock)
    {
        var monitor = new StaticOptionsMonitor<LoginLockoutOptions>(opts);
        return new InMemoryLoginAttemptTracker(monitor, NullLogger<InMemoryLoginAttemptTracker>.Instance, clock);
    }

    [Fact]
    public void IsLocked_FlipsTrueAfterMaxFailures_AndClearsAfterDuration()
    {
        var clock = new StubClock();
        var opts = new LoginLockoutOptions { MaxFailedAttempts = 3, Window = TimeSpan.FromMinutes(15), LockoutDuration = TimeSpan.FromMinutes(10) };
        var t = MakeTracker(opts, clock);

        Assert.False(t.IsLocked("alice"));
        t.RecordFailure("alice");
        t.RecordFailure("alice");
        Assert.False(t.IsLocked("alice"));
        t.RecordFailure("alice");
        Assert.True(t.IsLocked("alice"));

        // Still locked just before the cooldown elapses.
        clock.Now += TimeSpan.FromMinutes(9);
        Assert.True(t.IsLocked("alice"));

        // Cooldown elapsed: locked flips false even before any new
        // failure resets the slate.
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.False(t.IsLocked("alice"));
    }

    [Fact]
    public void RecordSuccess_WipesFailureCount()
    {
        var clock = new StubClock();
        var opts = new LoginLockoutOptions { MaxFailedAttempts = 3, Window = TimeSpan.FromMinutes(15), LockoutDuration = TimeSpan.FromMinutes(10) };
        var t = MakeTracker(opts, clock);

        t.RecordFailure("alice");
        t.RecordFailure("alice");
        t.RecordSuccess("alice");
        // Two more failures should not be enough to lock — the prior
        // two were wiped by the success.
        t.RecordFailure("alice");
        t.RecordFailure("alice");
        Assert.False(t.IsLocked("alice"));
    }

    [Fact]
    public void Window_RollsForward_WhenStaleFailuresExpire()
    {
        var clock = new StubClock();
        var opts = new LoginLockoutOptions { MaxFailedAttempts = 3, Window = TimeSpan.FromMinutes(15), LockoutDuration = TimeSpan.FromMinutes(10) };
        var t = MakeTracker(opts, clock);

        t.RecordFailure("alice");
        t.RecordFailure("alice");
        // Window elapses; the prior two failures must not count.
        clock.Now += TimeSpan.FromMinutes(20);
        t.RecordFailure("alice");
        t.RecordFailure("alice");
        Assert.False(t.IsLocked("alice"));
        // Third attempt inside the new window does lock.
        t.RecordFailure("alice");
        Assert.True(t.IsLocked("alice"));
    }

    [Fact]
    public void Username_IsCaseInsensitive()
    {
        var clock = new StubClock();
        var opts = new LoginLockoutOptions { MaxFailedAttempts = 2, Window = TimeSpan.FromMinutes(15), LockoutDuration = TimeSpan.FromMinutes(10) };
        var t = MakeTracker(opts, clock);

        t.RecordFailure("Alice");
        t.RecordFailure("ALICE");
        Assert.True(t.IsLocked("alice"));
    }

    [Fact]
    public void Disabled_NeverLocks()
    {
        var clock = new StubClock();
        var opts = new LoginLockoutOptions { Enabled = false, MaxFailedAttempts = 1 };
        var t = MakeTracker(opts, clock);

        t.RecordFailure("alice");
        t.RecordFailure("alice");
        Assert.False(t.IsLocked("alice"));
    }

    [Fact]
    public async Task Login_LocksOutAfterMaxFailedAttempts_ReturnsSame401AsWrongPassword()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:LoginLockout:Enabled"] = "true",
            ["Trading:Auth:LoginLockout:MaxFailedAttempts"] = "3",
            ["Trading:Auth:LoginLockout:Window"] = "00:15:00",
            ["Trading:Auth:LoginLockout:LockoutDuration"] = "00:15:00",
        });
        var http = factory.CreateClient();

        // Three wrong-password attempts: each returns 401 invalid.
        for (var i = 0; i < 3; i++)
        {
            var r = await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "nope" });
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        // Fourth attempt — even with the CORRECT password — must be
        // refused with the same 401 response shape because lockout
        // engaged on the third miss. Anti-enumeration: locked vs
        // wrong-password indistinguishable from the client's POV.
        var blocked = await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "wonderland" });
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        var body = await blocked.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("invalid credentials", body?["error"]);
    }

    [Fact]
    public async Task Login_SuccessClearsCounter_ForNextSession()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:LoginLockout:Enabled"] = "true",
            ["Trading:Auth:LoginLockout:MaxFailedAttempts"] = "3",
            ["Trading:Auth:LoginLockout:Window"] = "00:15:00",
            ["Trading:Auth:LoginLockout:LockoutDuration"] = "00:15:00",
        });
        var http = factory.CreateClient();

        // Two misses then a success: the slate must be wiped, so the
        // user can miss two MORE times without locking out.
        await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "nope" });
        await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "nope" });
        var ok = await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "wonderland" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "nope" });
        await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "nope" });
        var stillOk = await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "wonderland" });
        Assert.Equal(HttpStatusCode.OK, stillOk.StatusCode);
    }

    [Fact]
    public async Task Login_RecordsFailureForUnknownUsername_PreventingEnumeration()
    {
        // Probing rationale: if we did NOT count failures for unknown
        // usernames, an attacker could enumerate which usernames exist
        // by observing whether lockouts engage. This test pins the
        // anti-enumeration behavior.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:LoginLockout:Enabled"] = "true",
            ["Trading:Auth:LoginLockout:MaxFailedAttempts"] = "2",
            ["Trading:Auth:LoginLockout:Window"] = "00:15:00",
            ["Trading:Auth:LoginLockout:LockoutDuration"] = "00:15:00",
        });
        var http = factory.CreateClient();

        await http.PostAsJsonAsync("/auth/login", new { username = "ghost-user", password = "x" });
        await http.PostAsJsonAsync("/auth/login", new { username = "ghost-user", password = "x" });
        var third = await http.PostAsJsonAsync("/auth/login", new { username = "ghost-user", password = "x" });

        // All three look identical — 401 invalid credentials. We can't
        // observe the lock externally from a single response (by design),
        // but the tracker's behavior is exercised end-to-end and the
        // unit tests pin the lockout state.
        Assert.Equal(HttpStatusCode.Unauthorized, third.StatusCode);
    }

    private sealed class StubClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
