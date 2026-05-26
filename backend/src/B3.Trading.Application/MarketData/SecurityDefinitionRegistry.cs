using System.Collections.Concurrent;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// OPT-D (#486, refs #454 Fase 2). Thread-safe, in-process registry
/// of <see cref="InstrumentSpec"/>s sourced from the upstream
/// <c>SecurityDefinition</c> WebSocket channel
/// (<c>pedrosakuma/B3MarketDataPlatform#55</c> / SDK 0.5.0). The host
/// adapter (<c>SdkMarketDataSubscriber</c>) translates each
/// <c>SecurityDefinitionEvent</c> the SDK raises into an
/// <see cref="InstrumentSpec"/> + <c>SecurityId</c> and calls
/// <see cref="Upsert(string, InstrumentSpec, ulong)"/>. Consumers
/// (currently <see cref="SymbolDirectory.TryGetSpec(string?, out InstrumentSpec)"/>
/// via the overlay ctor parameter) check the registry FIRST and only
/// fall back to the operator-configured static directory when the
/// registry has no entry — which happens before the first
/// <c>SecurityDefinition</c> frame for that symbol arrives and any
/// time the kill-switch <c>Trading:MarketData:EnableSecurityDefinition</c>
/// is set to <c>false</c>.
///
/// <para>
/// Concurrency contract: a single SDK callback thread writes (the SDK
/// guarantees ordered, single-threaded event dispatch); arbitrary
/// hot-path threads read on every order submit and risk check. A
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> with
/// <see cref="StringComparer.OrdinalIgnoreCase"/> gives us lock-free
/// reads and the same case-insensitive lookup semantics the static
/// directory uses.
/// </para>
///
/// <para>
/// "Registry wins" semantics: when a symbol is present in both the
/// registry and the static directory, the registry entry replaces the
/// static one wholesale (no field-level merge). The rationale is that
/// venue-pushed data is more authoritative than hand-typed YAML; if
/// an operator needs to override (e.g. roll out an emergency tick
/// hot-fix), they set <c>EnableSecurityDefinition=false</c> and lean
/// on the static directory exclusively. This is symmetric with how
/// <c>MarketDataReferencePrice</c> overlays the static
/// <c>ConfigReferencePrice</c>.
/// </para>
/// </summary>
public sealed class SecurityDefinitionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _bySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces (or installs) the spec for <paramref name="symbol"/>.
    /// The SDK's <c>SecurityDefinitionEvent</c> ships both a bootstrap
    /// snapshot at subscribe time and deltas on every real change;
    /// <see cref="Upsert(string, InstrumentSpec, ulong)"/> is the
    /// single mutation path for both — duplicates are inherently safe
    /// because the registry stores the full spec, not deltas.
    /// </summary>
    public void Upsert(string symbol, InstrumentSpec spec, ulong securityId)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        _bySymbol[symbol] = new Entry(spec, securityId);
    }

    /// <summary>
    /// Registry-first spec lookup. Returns <c>false</c> when no
    /// upstream <c>SecurityDefinition</c> frame has been observed for
    /// the symbol; callers (currently <see cref="SymbolDirectory"/>)
    /// then fall back to the static, operator-configured directory.
    /// </summary>
    public bool TryGetSpec(string? symbol, out InstrumentSpec spec)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            spec = default;
            return false;
        }
        if (_bySymbol.TryGetValue(symbol, out var entry))
        {
            spec = entry.Spec;
            return true;
        }
        spec = default;
        return false;
    }

    /// <summary>
    /// Returns the <c>SecurityId</c> the venue declared for the
    /// symbol in its <c>SecurityDefinition_12</c> frame. Used as a
    /// future hook for the FIXP adapter's inverse lookup (#171 E) so
    /// dynamically-onboarded option series don't have to be
    /// pre-listed in <c>Trading:SymbolDirectory:SecurityIds</c>;
    /// today only the spec side of the projection is wired through
    /// the host.
    /// </summary>
    public bool TryGetSecurityId(string? symbol, out ulong securityId)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            securityId = 0;
            return false;
        }
        if (_bySymbol.TryGetValue(symbol, out var entry))
        {
            securityId = entry.SecurityId;
            return true;
        }
        securityId = 0;
        return false;
    }

    /// <summary>
    /// Number of symbols currently held by the registry. Exposed for
    /// startup logging and for tests that assert "the SDK frame was
    /// projected" without binding to a specific symbol.
    /// </summary>
    public int Count => _bySymbol.Count;

    private readonly record struct Entry(InstrumentSpec Spec, ulong SecurityId);

    /// <summary>
    /// OPT-D (#486). Side-table for projecting raw SBE numerics
    /// (PutOrCall / ExerciseStyle / MaturityDate / StrikePrice +
    /// PriceDivisor) coming over the wire on
    /// <c>SecurityDefinition_12</c> into a typed
    /// <see cref="OptionMetadata"/>. SDK-agnostic on purpose — the
    /// host adapter passes primitives so Application can unit-test
    /// the translation without a SDK package dependency. Returns
    /// <c>null</c> when any minimum-viable field is missing or
    /// invalid (the spec degrades gracefully to equity-like;
    /// callers may still upsert tick + lot).
    /// </summary>
    public static OptionMetadata? TryProject(
        long? contractMultiplier,
        long? maturityDate,
        long? putOrCall,
        long? exerciseStyle,
        long? strikePrice,
        long? priceDivisor,
        string? underlyingAsset)
    {
        if (contractMultiplier is not { } rawMult || rawMult <= 0) return null;
        if (maturityDate is not { } rawMaturity) return null;
        if (putOrCall is not { } rawPc) return null;
        if (!TryMapPutOrCall(rawPc, out var pc)) return null;
        if (!TryMapExerciseStyle(exerciseStyle, out var exStyle)) return null;
        if (!TryParseMaturity(rawMaturity, out var expiration)) return null;

        var divisor = priceDivisor is { } d && d > 0 ? (decimal)d : 10000m;
        var strike = strikePrice is { } rawStrike && rawStrike > 0
            ? rawStrike / divisor
            : 0m;

        return new OptionMetadata(
            StrikePrice: strike,
            ExpirationDate: expiration,
            PutOrCall: pc,
            ExerciseStyle: exStyle,
            UnderlyingSymbol: underlyingAsset ?? string.Empty,
            ContractMultiplier: rawMult,
            OptPayoutType: OptPayoutType.Vanilla);
    }

    private static bool TryMapPutOrCall(long raw, out PutOrCall value)
    {
        // SBE PutOrCall convention shared with upstream
        // pedrosakuma/B3MatchingPlatform#473: 0 = Put, 1 = Call.
        switch (raw)
        {
            case 0: value = PutOrCall.Put; return true;
            case 1: value = PutOrCall.Call; return true;
            default: value = default; return false;
        }
    }

    private static bool TryMapExerciseStyle(long? raw, out ExerciseStyle value)
    {
        // SBE ExerciseStyle: 1 = European, 2 = American. Missing is
        // tolerated (defaults to American — B3's most-listed style)
        // so a venue partial frame doesn't drop the entire option
        // projection.
        switch (raw)
        {
            case null: value = ExerciseStyle.American; return true;
            case 1: value = ExerciseStyle.European; return true;
            case 2: value = ExerciseStyle.American; return true;
            default: value = default; return false;
        }
    }

    private static bool TryParseMaturity(long raw, out DateOnly value)
    {
        // YYYYMMDD encoding per SecurityDefinition_12 wire layout.
        var y = (int)(raw / 10000);
        var m = (int)((raw / 100) % 100);
        var d = (int)(raw % 100);
        if (y < 1970 || y > 2100 || m < 1 || m > 12 || d < 1 || d > 31)
        {
            value = default;
            return false;
        }
        try { value = new DateOnly(y, m, d); return true; }
        catch (ArgumentOutOfRangeException) { value = default; return false; }
    }
}

/// <summary>
/// OPT-D (#486). Static helper for projecting raw SBE numerics into
/// <see cref="OptionMetadata"/>. Lives outside
/// <see cref="SecurityDefinitionRegistry"/> for testability — wraps
/// the registry's nested translator without changing the public
/// surface.
/// </summary>
internal static class SecurityDefinitionTranslator
{
    public static OptionMetadata? TryProjectOption(
        long? contractMultiplier,
        long? maturityDate,
        long? putOrCall,
        long? exerciseStyle,
        long? strikePrice,
        long? priceDivisor,
        string? underlyingAsset)
        => SecurityDefinitionRegistry.TryProject(
            contractMultiplier, maturityDate, putOrCall, exerciseStyle,
            strikePrice, priceDivisor, underlyingAsset);
}
