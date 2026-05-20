using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;

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
///
/// <para>
/// <b>Halt origin (#370 Stage A):</b> halts carry a
/// <see cref="HaltOrigin"/> tag (<see cref="HaltOrigin.Operator"/>
/// for <c>/admin/halts</c>, <see cref="HaltOrigin.Venue"/> for halts
/// observed via market data). The two origins are independent flags:
/// a symbol is halted iff at least one origin has it halted, so an
/// operator halt is never cleared by a venue resume (operator stays
/// in control) and a venue halt is never cleared by an operator
/// resume (would create a false sense of safety while the venue is
/// still rejecting). See <see cref="HaltOrigin"/> for the rationale.
/// </para>
/// </summary>
public sealed class SymbolHaltService
{
    // Bit-flag per origin (Operator=1, Venue=2). A symbol is halted
    // iff its value is != 0. ConcurrentDictionary gives us atomic
    // reads for IsHalted on the risk-pipeline hot path without
    // taking a lock; mutations go through Halt/Resume which AddOrUpdate.
    private readonly ConcurrentDictionary<string, byte> _haltedSymbols =
        new(StringComparer.OrdinalIgnoreCase);

    private static byte FlagOf(HaltOrigin origin) => (byte)(1 << (int)origin);

    public bool IsHalted(string symbol) => _haltedSymbols.ContainsKey(symbol);

    /// <summary>True iff the symbol is halted with at least the
    /// given origin flag set. Used by callers (e.g. recovery) that
    /// need to reason about who placed the halt.</summary>
    public bool IsHaltedBy(string symbol, HaltOrigin origin)
    {
        if (_haltedSymbols.TryGetValue(symbol, out var flags))
            return (flags & FlagOf(origin)) != 0;
        return false;
    }

    /// <summary>
    /// Marks <paramref name="symbol"/> halted by <paramref name="origin"/>.
    /// Idempotent: re-halting with the same origin is a no-op. If the
    /// other origin already holds a halt, this adds to it; the symbol
    /// stays halted until BOTH origins resume.
    /// </summary>
    public void Halt(string symbol, HaltOrigin origin = HaltOrigin.Operator)
    {
        var flag = FlagOf(origin);
        _haltedSymbols.AddOrUpdate(symbol, flag, (_, existing) => (byte)(existing | flag));
    }

    /// <summary>
    /// Clears the <paramref name="origin"/> flag for
    /// <paramref name="symbol"/>. Returns true iff the symbol was
    /// halted by that origin and is now fully cleared (i.e. no other
    /// origin still holds it halted). Returns false when either the
    /// symbol was not halted by this origin, or it remains halted by
    /// the other origin.
    /// </summary>
    public bool Resume(string symbol, HaltOrigin origin = HaltOrigin.Operator)
    {
        var flag = FlagOf(origin);
        while (_haltedSymbols.TryGetValue(symbol, out var existing))
        {
            if ((existing & flag) == 0) return false; // not halted by this origin
            var next = (byte)(existing & ~flag);
            if (next == 0)
            {
                if (_haltedSymbols.TryRemove(new KeyValuePair<string, byte>(symbol, existing)))
                    return true;
            }
            else
            {
                if (_haltedSymbols.TryUpdate(symbol, next, existing))
                    return false; // still halted by the other origin
            }
            // CAS lost a race — retry.
        }
        return false;
    }

    public IReadOnlyCollection<string> ListHalted() => _haltedSymbols.Keys.ToArray();

    /// <summary>
    /// Returns the current halt set along with each symbol's origin
    /// flags (Operator=1, Venue=2, both=3). Used by the snapshotter
    /// to capture origin alongside the symbol list.
    /// </summary>
    public IReadOnlyCollection<SymbolHaltEntry> ListHaltedWithOrigin()
    {
        var snapshot = _haltedSymbols.ToArray();
        var entries = new SymbolHaltEntry[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
            entries[i] = new SymbolHaltEntry(snapshot[i].Key, snapshot[i].Value);
        return entries;
    }

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

    /// <summary>
    /// Raw snapshot with origin flags. New format added by #370 Stage A;
    /// see <see cref="ListHaltedWithOrigin"/>.
    /// </summary>
    public SymbolHaltEntry[] RawSnapshotWithOrigin()
    {
        var snapshot = _haltedSymbols.ToArray();
        if (snapshot.Length == 0) return Array.Empty<SymbolHaltEntry>();
        var entries = new SymbolHaltEntry[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
            entries[i] = new SymbolHaltEntry(snapshot[i].Key, snapshot[i].Value);
        return entries;
    }

    public void Restore(IEnumerable<string> haltedSymbols)
    {
        ArgumentNullException.ThrowIfNull(haltedSymbols);
        _haltedSymbols.Clear();
        // Legacy path — pre-#370 snapshots only carried the symbol
        // list, no origin. Treat them as operator halts so the
        // existing /admin/halts DELETE keeps working after recovery.
        var operatorFlag = FlagOf(HaltOrigin.Operator);
        foreach (var s in haltedSymbols) _haltedSymbols[s] = operatorFlag;
    }

    /// <summary>
    /// Restore with origin flags. Used by the snapshotter when
    /// replaying post-#370 snapshots.
    /// </summary>
    public void RestoreWithOrigin(IEnumerable<SymbolHaltEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _haltedSymbols.Clear();
        foreach (var e in entries)
        {
            if (e.Flags == 0) continue; // defensive: ignore cleared rows
            _haltedSymbols[e.Symbol] = e.Flags;
        }
    }
}

/// <summary>
/// A halted symbol with its origin bitmask
/// (Operator=1, Venue=2, both=3). Carried in snapshots so the
/// origin distinction survives recovery.
/// </summary>
public readonly record struct SymbolHaltEntry(string Symbol, byte Flags);
