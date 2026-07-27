namespace B3.Trading.MarketMakerBot;

internal sealed class ContiguousInboundSequenceTracker
{
    private readonly SortedSet<ulong> _pending = [];
    private readonly object _gate = new();
    private ulong _contiguous;

    public void Reset(ulong contiguous)
    {
        lock (_gate)
        {
            _contiguous = contiguous;
            _pending.Clear();
        }
    }

    public ulong? Observe(ulong seqNum)
    {
        lock (_gate)
        {
            if (seqNum <= _contiguous)
                return null;
            if (_contiguous == ulong.MaxValue)
                throw new OverflowException("The contiguous inbound sequence is exhausted.");
            if (seqNum > _contiguous + 1)
            {
                _pending.Add(seqNum);
                return null;
            }

            _contiguous = seqNum;
            while (_pending.Remove(_contiguous + 1))
                _contiguous++;
            return _contiguous;
        }
    }
}
