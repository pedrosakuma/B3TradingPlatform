using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application.Risk;

/// <summary>
/// Tracks the current trading <see cref="SessionPhase"/> for the venue
/// (default) and per-symbol overrides. Backs <see cref="Checks.SessionPhaseCheck"/>.
///
/// <para><b>Hierarchy:</b> a per-symbol override always wins; otherwise
/// the global <see cref="DefaultPhase"/> applies. This matches B3
/// reality where the venue moves through phases as a whole but a
/// single ticker can be pinned in a circuit-breaker auction while the
/// rest stays continuous.</para>
///
/// <para><b>Default-of-default:</b> a fresh process starts at
/// <see cref="SessionPhase.Continuous"/>. This is fail-open and
/// chosen for back-compat — every existing test path implicitly
/// assumes "trading is on". Production deployments that want the
/// stricter posture (start <see cref="SessionPhase.Closed"/> until
/// operator/feed flips it) configure that via <c>Trading:SessionPhase:Default</c>
/// in the host. Phase changes are persisted via <c>SessionPhaseChangedEvent</c>
/// so a restart restores the last known state — losing a non-Continuous
/// phase on crash would silently revert to the least restrictive mode.</para>
///
/// <para>Symbol comparisons are case-insensitive (PETR4 == petr4) to
/// match the rest of the platform's symbol handling.</para>
/// </summary>
public sealed class SessionPhaseService
{
    private readonly ConcurrentDictionary<string, SessionPhase> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    private SessionPhase _default;

    public SessionPhaseService(SessionPhase defaultPhase = SessionPhase.Continuous)
    {
        _default = defaultPhase;
    }

    /// <summary>Current global default phase (applied when no symbol override is set).</summary>
    public SessionPhase DefaultPhase => _default;

    /// <summary>Resolves the effective phase for <paramref name="symbol"/>: override if any, else default.</summary>
    public SessionPhase GetPhase(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return _overrides.TryGetValue(symbol, out var p) ? p : _default;
    }

    /// <summary>Sets a per-symbol override.</summary>
    public void SetPhase(string symbol, SessionPhase phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        _overrides[symbol] = phase;
    }

    /// <summary>Removes the per-symbol override; the symbol falls back to <see cref="DefaultPhase"/>.</summary>
    public bool ClearPhase(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return _overrides.TryRemove(symbol, out _);
    }

    /// <summary>Sets the global default phase (applies to symbols without an explicit override).</summary>
    public void SetDefaultPhase(SessionPhase phase) => _default = phase;

    /// <summary>Snapshot of all per-symbol overrides for capture.</summary>
    public IReadOnlyDictionary<string, SessionPhase> ListOverrides() =>
        _overrides.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces overrides + default from a snapshot. Used during recovery.</summary>
    public void Restore(SessionPhase defaultPhase, IEnumerable<KeyValuePair<string, SessionPhase>> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _default = defaultPhase;
        _overrides.Clear();
        foreach (var kv in overrides)
            _overrides[kv.Key] = kv.Value;
    }
}
