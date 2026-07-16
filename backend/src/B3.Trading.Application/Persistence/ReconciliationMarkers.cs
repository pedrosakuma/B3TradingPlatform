using System.Collections.Concurrent;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.Persistence;

public enum ReconciliationMarkerKind
{
    CancelPreSend,
    ReplacePreSend,
    ReplaceAmbiguous,
}

/// <summary>
/// Durable sidecar written before resolving a WAL-backed outbound intent.
/// It survives the case where the resolution event cannot enter the WAL.
/// </summary>
public sealed record ReconciliationMarker(
    ReconciliationMarkerKind Kind,
    ulong OriginalClOrdId,
    ulong MutationClOrdId,
    string OwnerEndClientId,
    decimal NewRemainingNotional = 0m,
    DateTimeOffset? AmbiguousAtUtc = null)
{
    public string Id => $"{Kind}-{MutationClOrdId}";
}

public interface IReconciliationMarkerStore
{
    void Persist(ReconciliationMarker marker);
    void Remove(string markerId);
    IReadOnlyList<ReconciliationMarker> Load();
}

public sealed class ReconciliationMarkerPersistException : IOException
{
    public bool DurablyPublished { get; }

    public ReconciliationMarkerPersistException(
        string message,
        bool durablyPublished,
        Exception innerException)
        : base(message, innerException)
    {
        DurablyPublished = durablyPublished;
    }
}

public sealed class InMemoryReconciliationMarkerStore : IReconciliationMarkerStore
{
    private readonly ConcurrentDictionary<string, ReconciliationMarker> _markers =
        new(StringComparer.Ordinal);

    public void Persist(ReconciliationMarker marker) => _markers[marker.Id] = marker;
    public void Remove(string markerId) => _markers.TryRemove(markerId, out _);
    public IReadOnlyList<ReconciliationMarker> Load() => _markers.Values.ToArray();
}

public readonly record struct ReconciliationResolutionResult(
    bool Durable,
    bool MarkerDurable,
    Exception? Failure);

/// <summary>
/// Sidecar-first resolution writer. Backpressure is retried; the marker is
/// removed only after the WAL resolution has flushed durably.
/// </summary>
public sealed class ReconciliationResolutionWriter
{
    private const int BackpressureAttempts = 3;
    private readonly IReconciliationMarkerStore _markers;
    private readonly EventDispatcher _dispatcher;
    private readonly ILogger<ReconciliationResolutionWriter> _logger;

    public ReconciliationResolutionWriter(
        IReconciliationMarkerStore markers,
        EventDispatcher dispatcher,
        ILogger<ReconciliationResolutionWriter> logger)
    {
        _markers = markers;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<ReconciliationResolutionResult> ResolveAsync(
        ReconciliationMarker marker,
        WalEvent resolutionEvent,
        Action apply)
    {
        var markerDurable = false;
        Exception? markerFailure = null;
        try
        {
            _markers.Persist(marker);
            markerDurable = true;
        }
        catch (ReconciliationMarkerPersistException ex)
        {
            markerDurable = ex.DurablyPublished;
            markerFailure = ex;
        }
        catch (Exception ex)
        {
            markerFailure = ex;
        }

        for (var attempt = 1; attempt <= BackpressureAttempts; attempt++)
        {
            try
            {
                _dispatcher.Dispatch(resolutionEvent, apply);
            }
            catch (WalBackpressureException) when (attempt < BackpressureAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt))
                    .ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                return new ReconciliationResolutionResult(
                    false, markerDurable, Combine(markerFailure, ex));
            }

            try
            {
                await _dispatcher.FlushAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ReconciliationResolutionResult(
                    false, markerDurable, Combine(markerFailure, ex));
            }

            try
            {
                _markers.Remove(marker.Id);
            }
            catch (Exception ex)
            {
                // Stale sidecars are safe: startup observes the WAL-applied
                // state, removes the marker, and does not drain.
                _logger.LogWarning(ex,
                    "Durable resolution {MarkerId} committed but sidecar cleanup failed.",
                    marker.Id);
            }
            return new ReconciliationResolutionResult(true, markerDurable, null);
        }

        throw new InvalidOperationException("Resolution retry loop exhausted unexpectedly.");
    }

    private static Exception Combine(Exception? markerFailure, Exception walFailure) =>
        markerFailure is null
            ? walFailure
            : new AggregateException(markerFailure, walFailure);
}

/// <summary>
/// Applies unresolved reconciliation sidecars after snapshot + WAL replay.
/// Resolved/stale sidecars are removed; unresolved markers force drain.
/// </summary>
public sealed class ReconciliationMarkerRecovery
{
    private readonly IReconciliationMarkerStore _store;
    private readonly EventDispatcher _dispatcher;
    private readonly PendingCancelRegistry _pendingCancels;
    private readonly PendingReplacementRegistry _replacements;
    private readonly OrderOwnershipMap _ownership;
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly IUserBotOrderMappingRegistry? _botMappings;
    private readonly IDrainController _drain;
    private readonly ILogger<ReconciliationMarkerRecovery> _logger;

    public ReconciliationMarkerRecovery(
        IReconciliationMarkerStore store,
        EventDispatcher dispatcher,
        PendingCancelRegistry pendingCancels,
        PendingReplacementRegistry replacements,
        OrderOwnershipMap ownership,
        ClOrdIdPrefixRegistry clOrdIds,
        IDrainController drain,
        ILogger<ReconciliationMarkerRecovery> logger,
        IUserBotOrderMappingRegistry? botMappings = null)
    {
        _store = store;
        _dispatcher = dispatcher;
        _pendingCancels = pendingCancels;
        _replacements = replacements;
        _ownership = ownership;
        _clOrdIds = clOrdIds;
        _drain = drain;
        _logger = logger;
        _botMappings = botMappings;
    }

    public int Apply()
    {
        IReadOnlyList<ReconciliationMarker> markers;
        try
        {
            markers = _store.Load();
        }
        catch (Exception ex)
        {
            _drain.BeginDrain("reconciliation_marker_store_invalid");
            _logger.LogCritical(ex,
                "Reconciliation marker store is corrupt or unreadable; ingress remains drained.");
            return 1;
        }

        var unresolved = 0;
        foreach (var marker in markers)
        {
            _clOrdIds.AdvanceCounterTo(
                new EndClientId(marker.OwnerEndClientId),
                marker.MutationClOrdId);
            if (IsAlreadyResolved(marker))
            {
                _store.Remove(marker.Id);
                continue;
            }

            _dispatcher.RunExclusive(() => ApplyUnresolved(marker));
            unresolved++;
            _logger.LogCritical(
                "Unresolved outbound reconciliation marker {MarkerId} recovered; ingress remains drained.",
                marker.Id);
        }

        if (unresolved > 0)
            _drain.BeginDrain("outbound_reconciliation_marker_recovered");
        return unresolved;
    }

    private bool IsAlreadyResolved(ReconciliationMarker marker) =>
        marker.Kind switch
        {
            ReconciliationMarkerKind.CancelPreSend =>
                !_pendingCancels.TryGetByCancel(marker.MutationClOrdId, out _),
            ReconciliationMarkerKind.ReplacePreSend =>
                !_replacements.TryGet(marker.MutationClOrdId, out _),
            ReconciliationMarkerKind.ReplaceAmbiguous =>
                _replacements.IsAmbiguous(marker.MutationClOrdId),
            _ => false,
        };

    private void ApplyUnresolved(ReconciliationMarker marker)
    {
        switch (marker.Kind)
        {
            case ReconciliationMarkerKind.CancelPreSend:
                _pendingCancels.TryConsumeByCancel(marker.MutationClOrdId, out _);
                _ownership.RemoveCancelLink(marker.MutationClOrdId);
                _botMappings?.ReapCancel(marker.MutationClOrdId);
                break;
            case ReconciliationMarkerKind.ReplacePreSend:
                _replacements.TryConsume(marker.MutationClOrdId, out _);
                _ownership.RemoveCancelLink(marker.MutationClOrdId);
                break;
            case ReconciliationMarkerKind.ReplaceAmbiguous:
                _replacements.MarkAmbiguousMarginHeld(
                    marker.MutationClOrdId,
                    marker.AmbiguousAtUtc ?? DateTimeOffset.UtcNow,
                    marker.NewRemainingNotional);
                break;
        }
    }
}

/// <summary>
/// Conservative cold-start fence for Wave 1. Any cancel/replace intent still
/// unresolved after snapshot + WAL + sidecar recovery has no current-process
/// send proof, so ingress remains closed pending operator/venue reconciliation.
/// </summary>
public sealed class ColdStartLifecycleGuard
{
    private readonly PendingCancelRegistry _pendingCancels;
    private readonly PendingReplacementRegistry _replacements;
    private readonly IDrainController _drain;
    private readonly ILogger<ColdStartLifecycleGuard> _logger;

    public ColdStartLifecycleGuard(
        PendingCancelRegistry pendingCancels,
        PendingReplacementRegistry replacements,
        IDrainController drain,
        ILogger<ColdStartLifecycleGuard> logger)
    {
        _pendingCancels = pendingCancels;
        _replacements = replacements;
        _drain = drain;
        _logger = logger;
    }

    public int Apply()
    {
        var cancelCount = _pendingCancels.Snapshot().Count;
        var replaceCount = _replacements.Snapshot().Count;
        var unresolved = cancelCount + replaceCount;
        if (unresolved == 0)
            return 0;

        _drain.BeginDrain("cold_start_unresolved_lifecycle_intents");
        _logger.LogCritical(
            "Cold recovery retained {CancelCount} cancel and {ReplaceCount} replace intents without current-process send proof; readiness remains closed.",
            cancelCount, replaceCount);
        return unresolved;
    }
}
