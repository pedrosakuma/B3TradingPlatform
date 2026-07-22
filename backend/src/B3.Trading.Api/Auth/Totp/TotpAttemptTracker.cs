using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth.Totp;

/// <summary>
/// Per-user rate limit for /api/auth/2fa/verify (#303). Mirrors
/// <see cref="ILoginAttemptTracker"/> shape but tracks a separate
/// counter so a TOTP-brute flood does not lock the user out of
/// password login (and vice-versa). Returns a Retry-After hint so the
/// endpoint can emit a sensible 429.
/// </summary>
public interface ITotpAttemptTracker
{
    bool IsLocked(string username, out TimeSpan retryAfter);
    void RecordFailure(string username);
    void RecordSuccess(string username);
}

internal sealed class InMemoryTotpAttemptTracker : ITotpAttemptTracker
{
    private const int MaxTrackedUsernames = 50_000;

    private readonly IOptionsMonitor<TotpLockoutOptions> _options;
    private readonly ILogger<InMemoryTotpAttemptTracker> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, AttemptState> _state =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemoryTotpAttemptTracker(
        IOptionsMonitor<TotpLockoutOptions> options,
        ILogger<InMemoryTotpAttemptTracker> logger,
        TimeProvider clock)
    {
        _options = options;
        _logger = logger;
        _clock = clock;
    }

    public bool IsLocked(string username, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var opts = _options.CurrentValue;
        if (!opts.Enabled || string.IsNullOrEmpty(username)) return false;
        if (!_state.TryGetValue(username, out var st)) return false;
        lock (st)
        {
            if (st.LockedUntil is { } until && until > _clock.GetUtcNow())
            {
                retryAfter = until - _clock.GetUtcNow();
                if (retryAfter < TimeSpan.FromSeconds(1)) retryAfter = TimeSpan.FromSeconds(1);
                return true;
            }
        }
        return false;
    }

    public void RecordFailure(string username)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || string.IsNullOrEmpty(username)) return;

        var now = _clock.GetUtcNow();
        var state = _state.GetOrAdd(username, _ => new AttemptState());
        bool engaged = false;
        DateTimeOffset until = default;

        lock (state)
        {
            if (state.LockedUntil is { } existing && existing <= now)
            {
                state.LockedUntil = null;
                state.FirstFailure = default;
                state.FailureCount = 0;
            }
            if (state.LockedUntil is not null) return;
            if (state.FailureCount == 0 || now - state.FirstFailure > opts.Window)
            {
                state.FirstFailure = now;
                state.FailureCount = 0;
            }
            state.FailureCount++;
            if (state.FailureCount >= opts.MaxFailedAttempts)
            {
                state.LockedUntil = now + opts.LockoutDuration;
                engaged = true;
                until = state.LockedUntil.Value;
            }
        }

        if (engaged)
        {
            _logger.LogInformation(
                "TOTP verify lockout engaged for username={Username} until {Until:o}.",
                username, until);
        }

        MaybePrune(now, opts);
    }

    public void RecordSuccess(string username)
    {
        if (string.IsNullOrEmpty(username)) return;
        _state.TryRemove(username, out _);
    }

    private void MaybePrune(DateTimeOffset now, TotpLockoutOptions opts)
    {
        if (_state.Count <= MaxTrackedUsernames) return;
        foreach (var kvp in _state)
        {
            var st = kvp.Value;
            bool evict;
            lock (st)
            {
                var lockoutClear = st.LockedUntil is null || st.LockedUntil <= now;
                var windowStale = st.FailureCount == 0 || now - st.FirstFailure > opts.Window;
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
