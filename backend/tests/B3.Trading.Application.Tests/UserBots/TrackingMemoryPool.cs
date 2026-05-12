using System.Buffers;
using System.Collections.Concurrent;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// RFC §5.5 / issue #230 test double. Wraps
/// <see cref="ArrayPool{T}.Shared"/> and tracks rent / return counts per
/// outstanding array so tests can assert the
/// <see cref="B3.Trading.Application.UserBots.BotOutboundBuffer"/>
/// single-disposer invariant: every rented array is returned exactly
/// once and never returned twice.
///
/// <para>Successor to the pre-#230 <c>TrackingMemoryPool</c>: that
/// double wrapped <see cref="MemoryPool{T}.Shared"/> and produced an
/// <see cref="IMemoryOwner{T}"/> wrapper per rent. After #230 the
/// production encoder rents raw arrays directly from
/// <see cref="ArrayPool{T}.Shared"/>; this double mirrors that path
/// so the dispose-count invariant translates exactly.</para>
/// </summary>
internal sealed class TrackingMemoryPool : ArrayPool<byte>, IDisposable
{
    private int _rentCount;
    private int _disposeCount;
    // We track each rented array by identity so a double-return
    // surfaces as a deterministic test failure instead of a silent
    // double-decrement on a shared counter.
    private readonly ConcurrentDictionary<byte[], byte> _outstanding =
        new(ReferenceEqualityComparer<byte[]>.Instance);

    public int RentCount => Volatile.Read(ref _rentCount);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int OutstandingCount => _outstanding.Count;

    public override byte[] Rent(int minimumLength)
    {
        var arr = Shared.Rent(minimumLength);
        Interlocked.Increment(ref _rentCount);
        if (!_outstanding.TryAdd(arr, 0))
        {
            // The shared pool handed us back an array we already
            // believe is outstanding — that would mean we missed a
            // Return upstream. Fail loudly so the test surfaces it.
            throw new InvalidOperationException(
                "TrackingMemoryPool received a rent for an array that is still tracked as outstanding.");
        }
        return arr;
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        if (!_outstanding.TryRemove(array, out _))
            throw new InvalidOperationException("OutboundFrame double-return detected by TrackingMemoryPool.");
        Interlocked.Increment(ref _disposeCount);
        Shared.Return(array, clearArray);
    }

    public void Dispose() { /* nothing — Shared owns the underlying inventory */ }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
