using System.Buffers;
using System.Collections.Concurrent;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// RFC §5.5 / issue #201 test double. Wraps
/// <see cref="MemoryPool{T}.Shared"/> and tracks rent / dispose counts
/// per outstanding owner so tests can assert the
/// <see cref="B3.Trading.Application.UserBots.BotOutboundBuffer"/>
/// single-disposer invariant: every rented owner is disposed exactly
/// once and never disposed twice.
/// </summary>
internal sealed class TrackingMemoryPool : MemoryPool<byte>
{
    private int _rentCount;
    private int _disposeCount;
    // We track each rented owner individually so a double-dispose
    // surfaces as a deterministic test failure instead of a silent
    // double-decrement on a shared counter.
    private readonly ConcurrentDictionary<TrackingOwner, byte> _outstanding = new();

    public int RentCount => Volatile.Read(ref _rentCount);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int OutstandingCount => _outstanding.Count;
    public override int MaxBufferSize => Shared.MaxBufferSize;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        var inner = Shared.Rent(minBufferSize);
        Interlocked.Increment(ref _rentCount);
        var wrapper = new TrackingOwner(this, inner);
        _outstanding[wrapper] = 0;
        return wrapper;
    }

    internal void OnOwnerDisposed(TrackingOwner owner)
    {
        if (!_outstanding.TryRemove(owner, out _))
            throw new InvalidOperationException("OutboundFrame double-dispose detected by TrackingMemoryPool.");
        Interlocked.Increment(ref _disposeCount);
    }

    protected override void Dispose(bool disposing) { /* nothing — Shared owns the underlying */ }

    internal sealed class TrackingOwner : IMemoryOwner<byte>
    {
        private readonly TrackingMemoryPool _pool;
        private IMemoryOwner<byte>? _inner;
        public TrackingOwner(TrackingMemoryPool pool, IMemoryOwner<byte> inner)
        {
            _pool = pool;
            _inner = inner;
        }
        public Memory<byte> Memory => (_inner ?? throw new ObjectDisposedException(nameof(TrackingOwner))).Memory;
        public void Dispose()
        {
            var inner = Interlocked.Exchange(ref _inner, null)
                ?? throw new InvalidOperationException("TrackingOwner double-dispose.");
            inner.Dispose();
            _pool.OnOwnerDisposed(this);
        }
    }
}
