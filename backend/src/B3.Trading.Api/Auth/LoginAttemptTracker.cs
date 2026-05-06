using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

/// <summary>
/// Tracks consecutive failed login attempts per username and decides
/// when an account is locked. The interface is deliberately silent:
/// callers translate <see cref="IsLocked"/> into the same 401 used for
/// wrong-password to avoid leaking which usernames exist or are locked.
/// </summary>
public interface ILoginAttemptTracker
{
    /// <summary>Returns <c>true</c> if <paramref name="username"/> is currently locked.</summary>
    bool IsLocked(string username);

    /// <summary>Record a failed attempt; may engage lockout.</summary>
    void RecordFailure(string username);

    /// <summary>Reset failure count after a successful authentication.</summary>
    void RecordSuccess(string username);
}

/// <summary>
/// Process-local in-memory implementation. State is intentionally not
/// persisted — restart clears lockouts, which is acceptable for v1
/// (restarts are rare and operators may need an emergency unlock by
/// design). Multi-host coordination is out of scope until the auth
/// stack is centralized.
/// </summary>
internal sealed class InMemoryLoginAttemptTracker : ILoginAttemptTracker
{
    // Soft cap on tracked usernames. We grow lazily but prune aggressively
    // when we cross the cap so a flood of distinct usernames cannot drive
    // the host OOM. 50k is generous: at ~80 bytes per entry that's ~4 MB.
    private const int MaxTrackedUsernames = 50_000;

    private readonly IOptionsMonitor<LoginLockoutOptions> _options;
    private readonly ILogger<InMemoryLoginAttemptTracker> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, AttemptState> _state =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemoryLoginAttemptTracker(
        IOptionsMonitor<LoginLockoutOptions> options,
        ILogger<InMemoryLoginAttemptTracker> logger,
        TimeProvider clock)
    {
        _options = options;
        _logger = logger;
        _clock = clock;
    }

    public bool IsLocked(string username)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || string.IsNullOrEmpty(username)) return false;
        if (!_state.TryGetValue(username, out var st)) return false;
        lock (st)
        {
            return st.LockedUntil is { } until && until > _clock.GetUtcNow();
        }
    }

    public void RecordFailure(string username)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || string.IsNullOrEmpty(username)) return;

        var now = _clock.GetUtcNow();
        var state = _state.GetOrAdd(username, _ => new AttemptState());

        bool engagedLockout = false;
        DateTimeOffset lockedUntilForLog = default;

        lock (state)
        {
            // Clear an expired lockout before counting fresh failures so
            // the user gets a full new window after their cooldown.
            if (state.LockedUntil is { } existing && existing <= now)
            {
                state.LockedUntil = null;
                state.FirstFailure = default;
                state.FailureCount = 0;
            }

            // Already locked: we don't extend the lockout based on
            // additional misses while inside the cooldown.
            if (state.LockedUntil is not null) return;

            // Sliding window: drop old failures.
            if (state.FailureCount == 0 || now - state.FirstFailure > opts.Window)
            {
                state.FirstFailure = now;
                state.FailureCount = 0;
            }

            state.FailureCount++;
            if (state.FailureCount >= opts.MaxFailedAttempts)
            {
                state.LockedUntil = now + opts.LockoutDuration;
                engagedLockout = true;
                lockedUntilForLog = state.LockedUntil.Value;
            }
        }

        if (engagedLockout)
        {
            // INFO is high enough for ops dashboards but doesn't include
            // the password attempt itself. Username is sensitive but is
            // already logged on signup; we mirror that level.
            _logger.LogInformation(
                "Login lockout engaged for username={Username} until {Until:o}.",
                username, lockedUntilForLog);
        }

        MaybePrune(now, opts);
    }

    public void RecordSuccess(string username)
    {
        if (string.IsNullOrEmpty(username)) return;
        _state.TryRemove(username, out _);
    }

    private void MaybePrune(DateTimeOffset now, LoginLockoutOptions opts)
    {
        if (_state.Count <= MaxTrackedUsernames) return;

        // Evict entries whose lockout has expired AND whose window has
        // also elapsed. Bounded scan; runs only past the cap so the
        // common path stays O(1).
        foreach (var kvp in _state)
        {
            var st = kvp.Value;
            bool evict;
            lock (st)
            {
                bool lockoutClear = st.LockedUntil is null || st.LockedUntil <= now;
                bool windowStale = st.FailureCount == 0 || now - st.FirstFailure > opts.Window;
                evict = lockoutClear && windowStale;
            }
            if (evict)
            {
                _state.TryRemove(kvp.Key, out _);
                if (_state.Count <= MaxTrackedUsernames * 9 / 10) break;
            }
        }
    }

    private sealed class AttemptState
    {
        public DateTimeOffset FirstFailure;
        public int FailureCount;
        public DateTimeOffset? LockedUntil;
    }
}
