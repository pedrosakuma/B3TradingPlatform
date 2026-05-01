using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application.Risk;

/// <summary>
/// In-memory kill-switch state, per-end-client and per-firm. Toggles take
/// effect on the very next risk evaluation — used by
/// <see cref="Checks.KillSwitchCheck"/>.
/// </summary>
public sealed class KillSwitchService
{
    private readonly ConcurrentDictionary<EndClientId, byte> _killedEndClients = new();
    private readonly ConcurrentDictionary<string, byte> _killedFirms =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsEndClientKilled(EndClientId owner) => _killedEndClients.ContainsKey(owner);
    public bool IsFirmKilled(string firmId) => _killedFirms.ContainsKey(firmId);

    public void KillEndClient(EndClientId owner) => _killedEndClients[owner] = 1;
    public bool ReviveEndClient(EndClientId owner) => _killedEndClients.TryRemove(owner, out _);

    public void KillFirm(string firmId) => _killedFirms[firmId] = 1;
    public bool ReviveFirm(string firmId) => _killedFirms.TryRemove(firmId, out _);

    public IReadOnlyCollection<string> ListKilledEndClients() =>
        _killedEndClients.Keys.Select(k => k.Value).ToArray();

    public IReadOnlyCollection<string> ListKilledFirms() => _killedFirms.Keys.ToArray();
}
