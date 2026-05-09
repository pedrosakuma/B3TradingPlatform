using System.Collections.Concurrent;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Per-user session counter for enforcing <c>MaxSessionsPerUser</c>.
/// Singleton. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// compare-and-swap.
/// </summary>
public sealed class UserSessionCounter
{
    private readonly ConcurrentDictionary<string, int> _counts = new();

    /// <summary>
    /// Attempts to increment the count for <paramref name="userId"/>.
    /// Returns <c>false</c> when the count has reached <paramref name="max"/>.
    /// </summary>
    public bool TryIncrement(string userId, int max)
    {
        while (true)
        {
            var current = _counts.GetOrAdd(userId, 0);
            if (current >= max) return false;
            if (_counts.TryUpdate(userId, current + 1, current))
                return true;
            // CAS failed — another thread changed it; retry
        }
    }

    /// <summary>Decrements the session count for <paramref name="userId"/>.</summary>
    public void Decrement(string userId)
    {
        while (true)
        {
            if (!_counts.TryGetValue(userId, out var current) || current <= 0)
            {
                // Already zero or missing — nothing to do
                return;
            }
            if (_counts.TryUpdate(userId, current - 1, current))
                return;
        }
    }

    /// <summary>Returns the total active session count across all users.</summary>
    public int TotalActiveSessions
    {
        get
        {
            var total = 0;
            foreach (var kv in _counts)
                total += kv.Value;
            return total;
        }
    }
}
