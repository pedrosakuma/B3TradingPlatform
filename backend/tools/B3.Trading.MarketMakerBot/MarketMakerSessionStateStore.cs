using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.State;
using System.Text.Json;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Serializes the SDK state store with bot-owned terminal evidence and the
/// contiguous public events processed by the bot. SDK 0.17.0 persists inbound
/// receive state before public channel delivery and records explicit cancel
/// acknowledgements under the cancel request's ClOrdID, so both need a
/// bot-owned durability fence.
/// </summary>
internal sealed class MarketMakerSessionStateStore : ISessionStateStore
{
    private readonly FileSessionStateStore _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _retiredOrders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentProcessRetirements = new(StringComparer.Ordinal);
    private readonly string _retirementsPath;
    private readonly string _inboundFencePath;
    private readonly string _reconciliationPath;
    private bool _retirementsLoaded;
    private bool _inboundFenceLoaded;
    private uint _inboundFenceSessionId;
    private uint _inboundFenceSessionVerId;
    private ulong _contiguousInboundSeqNum;

    public MarketMakerSessionStateStore(string directory)
    {
        _inner = new FileSessionStateStore(directory);
        _retirementsPath = Path.Combine(directory, "retired-orders.txt");
        _inboundFencePath = Path.Combine(directory, "contiguous-inbound.txt");
        _reconciliationPath = Path.Combine(directory, "reconciliation-required.json");
    }

    public async ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureRetirementsLoadedAsync(ct);
            await EnsureInboundFenceLoadedAsync(ct);
            return FilterRetired(await _inner.LoadAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureRetirementsLoadedAsync(ct);
            await EnsureInboundFenceLoadedAsync(ct);
            await EnsureInboundFenceSeededAsync(snapshot, ct);
            var filtered = FilterRetired(snapshot)!;
            await _inner.SaveAsync(filtered, ct);
            await CompactPriorProcessRetirementsAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (delta is OrderClosedDelta closed)
                await PersistRetirementAsync(closed.ClOrdID.ToString(), ct);
            await _inner.AppendDeltaAsync(delta, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureRetirementsLoadedAsync(ct);
            await EnsureInboundFenceLoadedAsync(ct);
            return FilterRetired(await _inner.ReplayAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CompactAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureRetirementsLoadedAsync(ct);
            await EnsureInboundFenceLoadedAsync(ct);
            var snapshot = FilterRetired(await _inner.ReplayAsync(ct));
            if (snapshot is not null)
            {
                await _inner.SaveAsync(snapshot, ct);
                await CompactPriorProcessRetirementsAsync(ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RetireOrderAsync(ulong clOrdId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await PersistRetirementAsync(clOrdId.ToString(), ct);
            await _inner.AppendDeltaAsync(new OrderClosedDelta(new ClOrdID(clOrdId)), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RecordContiguousInboundAsync(
        uint sessionId,
        uint sessionVerId,
        ulong contiguousSeqNum,
        CancellationToken ct = default)
    {
        if (sessionId == 0 || sessionVerId == 0 || contiguousSeqNum == 0)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureInboundFenceLoadedAsync(ct);
            if (MatchesInboundFence(sessionId, sessionVerId) &&
                contiguousSeqNum <= _contiguousInboundSeqNum)
                return;

            await PersistInboundFenceAsync(sessionId, sessionVerId, contiguousSeqNum, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RequireReconciliationAsync(string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _gate.WaitAsync(ct);
        try
        {
            var marker = new ReconciliationRequirement(DateTimeOffset.UtcNow, reason);
            var temporaryPath = _reconciliationPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(marker),
                ct);
            File.Move(temporaryPath, _reconciliationPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ReconciliationRequirement?> GetReconciliationRequirementAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_reconciliationPath))
                return null;
            var json = await File.ReadAllTextAsync(_reconciliationPath, ct);
            return JsonSerializer.Deserialize<ReconciliationRequirement>(json)
                ?? throw new InvalidDataException("The reconciliation-required marker is empty.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureRetirementsLoadedAsync(CancellationToken ct)
    {
        if (_retirementsLoaded)
            return;
        _retirementsLoaded = true;
        if (!File.Exists(_retirementsPath))
            return;

        await foreach (var line in File.ReadLinesAsync(_retirementsPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var separator = line.IndexOf('|');
            var clOrdId = separator < 0 ? line : line[..separator];
            _retiredOrders.Add(clOrdId);
        }
    }

    private async Task PersistRetirementAsync(string clOrdId, CancellationToken ct)
    {
        await EnsureRetirementsLoadedAsync(ct);
        _retiredOrders.Add(clOrdId);
        if (!_currentProcessRetirements.Add(clOrdId))
            return;
        await File.AppendAllTextAsync(
            _retirementsPath,
            clOrdId + Environment.NewLine,
            ct);
    }

    private async Task CompactPriorProcessRetirementsAsync(CancellationToken ct)
    {
        if (_retiredOrders.SetEquals(_currentProcessRetirements))
            return;

        _retiredOrders.Clear();
        _retiredOrders.UnionWith(_currentProcessRetirements);
        if (_currentProcessRetirements.Count == 0)
        {
            if (File.Exists(_retirementsPath))
                File.Delete(_retirementsPath);
            return;
        }

        await File.WriteAllLinesAsync(
            _retirementsPath,
            _currentProcessRetirements.Order(StringComparer.Ordinal),
            ct);
    }

    private SessionSnapshot? FilterRetired(SessionSnapshot? snapshot)
    {
        if (snapshot is null)
            return snapshot;

        var outstanding = snapshot.OutstandingOrders
            .Where(pair => !_retiredOrders.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return snapshot with
        {
            LastInboundSeqNum = MatchesInboundFence(snapshot.SessionId, snapshot.SessionVerId)
                ? _contiguousInboundSeqNum
                : snapshot.LastInboundSeqNum,
            OutstandingOrders = outstanding,
        };
    }

    private async Task EnsureInboundFenceSeededAsync(
        SessionSnapshot snapshot,
        CancellationToken ct)
    {
        if (snapshot.SessionId == 0 ||
            snapshot.SessionVerId == 0 ||
            MatchesInboundFence(snapshot.SessionId, snapshot.SessionVerId))
        {
            return;
        }

        // The SDK saves once after Establish and before starting its inbound
        // loop. Seed there so later receive-ahead snapshots cannot outrun the
        // bot's processed-event fence.
        await PersistInboundFenceAsync(
            snapshot.SessionId,
            snapshot.SessionVerId,
            snapshot.LastInboundSeqNum,
            ct);
    }

    private async Task PersistInboundFenceAsync(
        uint sessionId,
        uint sessionVerId,
        ulong contiguousSeqNum,
        CancellationToken ct)
    {
        var temporaryPath = _inboundFencePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            $"{sessionId}|{sessionVerId}|{contiguousSeqNum}",
            ct);
        File.Move(temporaryPath, _inboundFencePath, overwrite: true);
        _inboundFenceSessionId = sessionId;
        _inboundFenceSessionVerId = sessionVerId;
        _contiguousInboundSeqNum = contiguousSeqNum;
    }

    private bool MatchesInboundFence(uint sessionId, uint sessionVerId) =>
        sessionId != 0 &&
        sessionVerId != 0 &&
        sessionId == _inboundFenceSessionId &&
        sessionVerId == _inboundFenceSessionVerId;

    private async Task EnsureInboundFenceLoadedAsync(CancellationToken ct)
    {
        if (_inboundFenceLoaded)
            return;
        _inboundFenceLoaded = true;
        if (!File.Exists(_inboundFencePath))
            return;

        var value = await File.ReadAllTextAsync(_inboundFencePath, ct);
        var firstSeparator = value.IndexOf('|');
        var secondSeparator = firstSeparator < 0
            ? -1
            : value.IndexOf('|', firstSeparator + 1);
        if (firstSeparator <= 0 ||
            secondSeparator <= firstSeparator + 1 ||
            !uint.TryParse(value[..firstSeparator], out _inboundFenceSessionId) ||
            !uint.TryParse(
                value[(firstSeparator + 1)..secondSeparator],
                out _inboundFenceSessionVerId) ||
            !ulong.TryParse(value[(secondSeparator + 1)..], out _contiguousInboundSeqNum))
        {
            throw new InvalidDataException("The contiguous-inbound marker is invalid.");
        }
    }

}

internal sealed record ReconciliationRequirement(DateTimeOffset DetectedAtUtc, string Reason);
