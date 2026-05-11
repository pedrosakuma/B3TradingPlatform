using System.Collections.Concurrent;

namespace B3.Trading.Application.Risk;

/// <summary>
/// In-memory per-symbol trading halt state. Toggles take effect on the
/// very next risk evaluation — used by <see cref="Checks.SymbolHaltedCheck"/>.
///
/// <para>
/// Modelled per-symbol (no firm/end-client split) because in B3 cash
/// equities a halt is an instrument-level decision: when ITUB4 is in
/// circuit-breaker, no participant should be sending orders for it,
/// regardless of who they are. If a per-firm or per-symbol+firm slot
/// is ever needed (e.g. a single firm's gateway is degraded for one
/// ticker), it can be layered on top without changing this surface.
/// </para>
///
/// <para>
/// Symbol comparisons are case-insensitive (PETR4 == petr4) to match
/// the rest of the platform's symbol handling. The halted set is
/// captured by the snapshotter and replayed from
/// <c>SymbolHaltToggledEvent</c> records on recovery, so a halt
/// survives a process restart — losing it on crash would be the
/// worst possible default for a safety control.
/// </para>
/// </summary>
public sealed class SymbolHaltService
{
    private readonly ConcurrentDictionary<string, byte> _haltedSymbols =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsHalted(string symbol) => _haltedSymbols.ContainsKey(symbol);

    public void Halt(string symbol) => _haltedSymbols[symbol] = 1;
    public bool Resume(string symbol) => _haltedSymbols.TryRemove(symbol, out _);

    public IReadOnlyCollection<string> ListHalted() => _haltedSymbols.Keys.ToArray();

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8 / P6). Same data as <see cref="ListHalted"/>, returned
    /// as <c>string[]</c> for direct stitching into the raw aggregate.
    /// </summary>
    public string[] RawSnapshot()
    {
        var keys = _haltedSymbols.Keys;
        if (keys.Count == 0) return Array.Empty<string>();
        var raw = new string[keys.Count];
        var i = 0;
        foreach (var k in keys)
        {
            if (i == raw.Length) break;
            raw[i++] = k;
        }
        return i == raw.Length ? raw : raw[..i];
    }

    public void Restore(IEnumerable<string> haltedSymbols)
    {
        ArgumentNullException.ThrowIfNull(haltedSymbols);
        _haltedSymbols.Clear();
        foreach (var s in haltedSymbols) _haltedSymbols[s] = 1;
    }
}
