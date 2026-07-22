using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application;

/// <summary>
/// Q4.7 (#307). In-memory projection of every fill the host has applied,
/// keyed by a stable per-fill id (<c>{ClOrdId}:{CumulativeQuantityAfterFill}</c>
/// — the same shape <see cref="Persistence.FeeAccruedEvent.FillRef"/>
/// already uses). Each entry carries the originating fill metadata and
/// the optional <see cref="BookTouchSnapshot"/> captured at the moment
/// the venue ER landed.
///
/// <para>
/// <b>Single source of truth for compliance reads.</b> The keeper is
/// folded inside <see cref="ExecutionReportProcessor.Apply"/> for every
/// <see cref="ExecKind.Fill"/> / <see cref="ExecKind.PartialFill"/> — on
/// both the live dispatch and the WAL replay paths — so cold restart
/// preserves the touch evidence by re-running the same fold over each
/// <see cref="Persistence.ExecutionReportReceivedEvent"/> with its
/// additive <c>BookTouch</c> field.
/// </para>
///
/// <para>
/// Not part of the structured snapshot envelope by design (mirrors the
/// <see cref="Audit.AuditLogKeeper"/> approach): the WAL itself is the
/// source of truth, and snapshot+restart rehydration is handled by an
/// audit-style WAL pre-pass in <c>PersistenceRecovery</c> for seq
/// <i>≤</i> snapshot.Seq, then the normal post-snapshot replay.
/// </para>
/// </summary>
public sealed class FillProjection
{
    private readonly ConcurrentDictionary<string, FillRecord> _byId =
        new(StringComparer.Ordinal);
    // Insertion-order queue used to drive FIFO eviction once Capacity
    // is hit. Held outside the dictionary to keep TryGet lock-free.
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly int _capacity;
    private readonly object _evictionGate = new();

    public FillProjection()
        : this(new FillProjectionOptions())
    {
    }

    public FillProjection(IOptions<FillProjectionOptions> options)
        : this(options?.Value ?? new FillProjectionOptions())
    {
    }

    private FillProjection(FillProjectionOptions options)
    {
        _capacity = options.Capacity > 0 ? options.Capacity : 1_000_000;
    }

    /// <summary>Total fills currently retained. Used by tests and by the recovery driver's log line.</summary>
    public int Count => _byId.Count;

    /// <summary>Configured cap; oldest insertions evict once this is hit. Exposed for tests + diagnostics.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Canonical fill id used as the dictionary key and exposed on
    /// <c>/api/fills/{id}/touch</c>. Stable across replays because both
    /// inputs are taken from the durable WAL ER event.
    /// </summary>
    public static string BuildId(ulong clOrdId, long cumulativeQuantityAfterFill)
        => $"{clOrdId}:{cumulativeQuantityAfterFill}";

    /// <summary>
    /// Records (or overwrites — fills are idempotent on replay) the fill
    /// metadata + touch snapshot. Called by the ER processor for every
    /// fill / partial-fill it applies, regardless of whether the fill
    /// arrived live or via WAL replay.
    /// </summary>
    public FillRecord Record(
        ulong clOrdId,
        long cumulativeQuantityAfterFill,
        EndClientId owner,
        string? firmId,
        string symbol,
        OrderSide side,
        long lastQuantity,
        decimal lastPrice,
        DateTimeOffset timestampUtc,
        BookTouchSnapshot? bookTouch)
    {
        var id = BuildId(clOrdId, cumulativeQuantityAfterFill);
        var record = new FillRecord(
            id,
            clOrdId,
            cumulativeQuantityAfterFill,
            owner,
            firmId,
            symbol,
            side,
            lastQuantity,
            lastPrice,
            timestampUtc,
            bookTouch);
        // Idempotency: a replayed ER overwrites with the same payload;
        // a later partial-fill collision would only happen if cumulative
        // quantity collides, which by construction it does not.
        var isNew = !_byId.ContainsKey(id);
        _byId[id] = record;
        if (isNew)
        {
            _insertionOrder.Enqueue(id);
            // Bounded FIFO eviction: a single writer wins the gate and
            // trims the queue until Count <= Capacity. Other writers
            // skip the trim — the queue may transiently overshoot by a
            // few entries, which is acceptable for a soft cap.
            if (_byId.Count > _capacity && Monitor.TryEnter(_evictionGate))
            {
                try
                {
                    while (_byId.Count > _capacity && _insertionOrder.TryDequeue(out var victim))
                    {
                        _byId.TryRemove(victim, out _);
                    }
                }
                finally
                {
                    Monitor.Exit(_evictionGate);
                }
            }
        }
        return record;
    }

    /// <summary>
    /// Same as <see cref="Record"/> but a no-op when an entry with the
    /// same id already exists. Used by the recovery pre-pass so a
    /// duplicate / retransmit ER persisted later in the WAL cannot
    /// clobber the original fill's <see cref="BookTouchSnapshot"/>
    /// (which was captured at the moment the real execution arrived,
    /// not at retransmit time). The live dispatch path uses
    /// <see cref="Record"/> directly because
    /// <see cref="ExecutionReportProcessor"/> already suppresses
    /// duplicate fills before reaching the projection.
    /// </summary>
    public FillRecord? RecordIfAbsent(
        ulong clOrdId,
        long cumulativeQuantityAfterFill,
        EndClientId owner,
        string? firmId,
        string symbol,
        OrderSide side,
        long lastQuantity,
        decimal lastPrice,
        DateTimeOffset timestampUtc,
        BookTouchSnapshot? bookTouch)
    {
        var id = BuildId(clOrdId, cumulativeQuantityAfterFill);
        if (_byId.ContainsKey(id)) return null;
        return Record(clOrdId, cumulativeQuantityAfterFill, owner, firmId, symbol,
            side, lastQuantity, lastPrice, timestampUtc, bookTouch);
    }

    public bool TryGet(string fillId, out FillRecord record)
    {
        if (string.IsNullOrEmpty(fillId))
        {
            record = default!;
            return false;
        }
        return _byId.TryGetValue(fillId, out record!);
    }
}

/// <summary>
/// Immutable record of a single fill exposed by <see cref="FillProjection"/>.
/// Carries enough metadata for the REST + WS surfaces to render the
/// touch payload and for firm-scope authorization in the read path.
/// </summary>
public sealed record FillRecord(
    string Id,
    ulong ClOrdId,
    long CumulativeQuantityAfterFill,
    EndClientId Owner,
    string? FirmId,
    string Symbol,
    OrderSide Side,
    long LastQuantity,
    decimal LastPrice,
    DateTimeOffset TimestampUtc,
    BookTouchSnapshot? BookTouch);
