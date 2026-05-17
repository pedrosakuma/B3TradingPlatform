using System.Collections.Concurrent;

namespace B3.Trading.Application;

/// <summary>
/// Pass-1 review (#295) P1#1 — POV restart-resilient state.
///
/// <para>
/// Persisted per-POV running state: how much cumulative market volume
/// this POV plan has observed since its <c>StartUtc</c>, and the UTC
/// instant of the last evaluation tick. The engine uses these to
/// compute slice targets as
/// <c>targetCum = marketVolumeSeen * participationRate</c>, which is
/// robust to process restarts because the baseline is carried in the
/// WAL/snapshot rather than recomputed from <see cref="MarketData.VolumeCurveEstimator"/>'s
/// transient in-memory buckets.
/// </para>
///
/// <para>
/// <b>Why this is not part of the <see cref="Algo"/> aggregate.</b>
/// <c>marketVolumeSeen</c> is engine-internal scheduling state, not
/// part of the parent's business identity. Keeping it in a side book
/// (a) lets us evolve the persistence shape independently of
/// <see cref="Persistence.AlgoCreatedEvent"/> /
/// <see cref="Persistence.AlgoSnapshot"/>, and (b) means tests that
/// only care about the parent's terminal state don't need to wire it.
/// </para>
///
/// <para>
/// <b>Lifecycle.</b> Updated by the engine on every POV evaluation
/// tick (emit OR skip). Persisted to the WAL only on emit (via
/// <see cref="Persistence.AlgoPovSlicedEvent"/>'s additive
/// <c>MarketVolumeSeen</c> + <c>LastEvaluateAtUtc</c> fields) and
/// captured by snapshots (via <c>PlatformSnapshot.PovProgress</c>).
/// On restart the engine restores from the most recent persisted
/// value and continues accumulating from <c>LastEvaluateAtUtc</c>;
/// between the last persisted tick and restart any intervening volume
/// is lost but the restored baseline is monotonically correct so the
/// algo will never under-slice on the pre-restart accumulation.
/// </para>
/// </summary>
public sealed class PovProgressBook
{
    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), PovProgress> _progress = new();

    public PovProgress? TryGet(string firmId, ulong algoId) =>
        _progress.TryGetValue((firmId, algoId), out var p) ? p : null;

    /// <summary>
    /// Last-write-wins. Replay applies events in seq order so the
    /// resulting state matches the most recently persisted slice.
    /// </summary>
    public void Set(string firmId, ulong algoId, long marketVolumeSeen, DateTimeOffset lastEvaluateAtUtc) =>
        _progress[(firmId, algoId)] = new PovProgress(marketVolumeSeen, lastEvaluateAtUtc);

    public IEnumerable<(string FirmId, ulong AlgoId, PovProgress Progress)> Snapshot()
    {
        foreach (var kv in _progress)
            yield return (kv.Key.FirmId, kv.Key.AlgoId, kv.Value);
    }

    public void Restore(IEnumerable<(string FirmId, ulong AlgoId, PovProgress Progress)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _progress.Clear();
        foreach (var r in rows)
            _progress[(r.FirmId, r.AlgoId)] = r.Progress;
    }
}

public readonly record struct PovProgress(long MarketVolumeSeen, DateTimeOffset LastEvaluateAtUtc);
