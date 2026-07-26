using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.State;
using System.Text.Json;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Serializes the SDK state store with bot-owned terminal evidence. SDK 0.17.0
/// persists an explicit cancel acknowledgement under the cancel request's
/// ClOrdID, not the original NEW's ClOrdID, so the original can otherwise
/// reappear in a later compacted snapshot.
/// </summary>
internal sealed class MarketMakerSessionStateStore : ISessionStateStore
{
    private readonly FileSessionStateStore _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ulong> _retiredOrders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _currentProcessRetirements = new(StringComparer.Ordinal);
    private readonly string _retirementsPath;
    private readonly string _reconciliationPath;
    private bool _retirementsLoaded;

    public MarketMakerSessionStateStore(string directory)
    {
        _inner = new FileSessionStateStore(directory);
        _retirementsPath = Path.Combine(directory, "retired-orders.txt");
        _reconciliationPath = Path.Combine(directory, "reconciliation-required.json");
    }

    public async ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureRetirementsLoadedAsync(ct);
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
            await _inner.SaveAsync(FilterRetired(snapshot)!, ct);
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
                await PersistRetirementAsync(closed.ClOrdID.ToString(), inboundSeqNum: 0, ct);
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

    public async ValueTask RetireOrderAsync(
        ulong clOrdId,
        ulong inboundSeqNum,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await PersistRetirementAsync(clOrdId.ToString(), inboundSeqNum, ct);
            await _inner.AppendDeltaAsync(new OrderClosedDelta(new ClOrdID(clOrdId)), ct);
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
            var inboundSeqNum = separator < 0 ||
                !ulong.TryParse(line[(separator + 1)..], out var parsedSeqNum)
                    ? 0
                    : parsedSeqNum;
            AddOrAdvance(_retiredOrders, clOrdId, inboundSeqNum);
        }
    }

    private async Task PersistRetirementAsync(
        string clOrdId,
        ulong inboundSeqNum,
        CancellationToken ct)
    {
        await EnsureRetirementsLoadedAsync(ct);
        AddOrAdvance(_retiredOrders, clOrdId, inboundSeqNum);
        if (_currentProcessRetirements.TryGetValue(clOrdId, out var currentSeqNum) &&
            currentSeqNum >= inboundSeqNum)
        {
            return;
        }
        _currentProcessRetirements[clOrdId] = inboundSeqNum;
        await File.AppendAllTextAsync(
            _retirementsPath,
            $"{clOrdId}|{inboundSeqNum}{Environment.NewLine}",
            ct);
    }

    private async Task CompactPriorProcessRetirementsAsync(CancellationToken ct)
    {
        if (_retiredOrders.Count == _currentProcessRetirements.Count &&
            _retiredOrders.All(pair =>
                _currentProcessRetirements.TryGetValue(pair.Key, out var seqNum) &&
                seqNum == pair.Value))
        {
            return;
        }

        _retiredOrders.Clear();
        foreach (var pair in _currentProcessRetirements)
            _retiredOrders.Add(pair.Key, pair.Value);
        if (_currentProcessRetirements.Count == 0)
        {
            if (File.Exists(_retirementsPath))
                File.Delete(_retirementsPath);
            return;
        }

        await File.WriteAllLinesAsync(
            _retirementsPath,
            _currentProcessRetirements
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}|{pair.Value}"),
            ct);
    }

    private SessionSnapshot? FilterRetired(SessionSnapshot? snapshot)
    {
        if (snapshot is null || _retiredOrders.Count == 0)
            return snapshot;

        var outstanding = snapshot.OutstandingOrders
            .Where(pair => !_retiredOrders.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return snapshot with
        {
            LastInboundSeqNum = Math.Max(
                snapshot.LastInboundSeqNum,
                _retiredOrders.Values.Max()),
            OutstandingOrders = outstanding,
        };
    }

    private static void AddOrAdvance(
        Dictionary<string, ulong> retirements,
        string clOrdId,
        ulong inboundSeqNum)
    {
        if (!retirements.TryGetValue(clOrdId, out var existing) ||
            inboundSeqNum > existing)
        {
            retirements[clOrdId] = inboundSeqNum;
        }
    }
}

internal sealed record ReconciliationRequirement(DateTimeOffset DetectedAtUtc, string Reason);
