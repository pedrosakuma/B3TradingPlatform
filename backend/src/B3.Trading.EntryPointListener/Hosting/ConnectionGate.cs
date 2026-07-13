using System.Collections.Concurrent;
using System.Net;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// #529 pre-auth abuse gate: global + per-IP concurrent-connection caps and
/// an exact-match IP allow/deny filter, evaluated in the accept loop before
/// any TLS/Negotiate bytes. Slots are released when the connection ends.
///
/// <para>All caps default permissive (0 = unlimited, empty filter), so the
/// gate is inert until an operator tightens it for public exposure.</para>
/// </summary>
internal sealed class ConnectionGate
{
    private readonly int _maxTotal;
    private readonly int _maxPerIp;
    private readonly HashSet<string>? _allow;
    private readonly HashSet<string>? _deny;
    private readonly ConcurrentDictionary<string, int> _perIp = new();
    private int _total;

    public ConnectionGate(EntryPointListenerOptions.ConnectionCapsOptions caps)
    {
        _maxTotal = Math.Max(0, caps.MaxConcurrentTotal);
        _maxPerIp = Math.Max(0, caps.MaxConcurrentPerIp);
        _allow = caps.AllowedIps.Count > 0 ? new HashSet<string>(caps.AllowedIps) : null;
        _deny = caps.DeniedIps.Count > 0 ? new HashSet<string>(caps.DeniedIps) : null;
    }

    /// <summary>True when the source IP is barred by the allow/deny lists.</summary>
    public bool IsBlocked(IPAddress ip)
    {
        var key = ip.ToString();
        if (_allow is not null) return !_allow.Contains(key);
        return _deny is not null && _deny.Contains(key);
    }

    /// <summary>
    /// Tries to reserve a connection slot. Returns false (with no reservation)
    /// when a cap is exceeded. Dispose the lease to release on close.
    /// </summary>
    public bool TryAcquire(IPAddress ip, out IDisposable lease)
    {
        var key = ip.ToString();
        if (_maxTotal > 0 && Volatile.Read(ref _total) >= _maxTotal)
        {
            lease = NoOp;
            return false;
        }

        if (_maxPerIp > 0 && _perIp.GetValueOrDefault(key) >= _maxPerIp)
        {
            lease = NoOp;
            return false;
        }

        Interlocked.Increment(ref _total);
        _perIp.AddOrUpdate(key, 1, static (_, c) => c + 1);
        lease = new Lease(this, key);
        return true;
    }

    private void Release(string key)
    {
        Interlocked.Decrement(ref _total);
        _perIp.AddOrUpdate(key, 0, static (_, c) => c - 1);
    }

    private static readonly IDisposable NoOp = new NoOpLease();
    private sealed class NoOpLease : IDisposable { public void Dispose() { } }

    private sealed class Lease(ConnectionGate gate, string key) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release(key);
        }
    }
}
