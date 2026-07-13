using System.Collections.Concurrent;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// OPT-E (#487). Venue-pushed dynamic price band for one symbol.
/// Sourced from the upstream <c>PriceBand</c> WebSocket channel
/// (<c>pedrosakuma/B3MarketDataPlatform#56</c> / SDK 0.6.0) and
/// consumed by <see cref="Risk.Checks.PriceBandCheck"/> as the
/// authoritative fat-finger envelope, replacing the static-config
/// collar on the symbols where the venue actually emits.
/// </summary>
public readonly record struct PriceBand(
    decimal Lower,
    decimal Upper,
    DateTimeOffset AsOfUtc);

/// <summary>
/// OPT-E (#487). Read-side seam for <see cref="PriceBand"/>. Pre-trade
/// risk checks consume this; production wires it to
/// <see cref="PriceBandRegistry"/> (live SDK projection), tests can
/// substitute an in-memory stub without bringing the SDK.
/// </summary>
public interface IPriceBandSource
{
    /// <summary>
    /// Returns the latest band the venue has emitted for
    /// <paramref name="symbol"/>. <c>false</c> means the SDK has not
    /// yet observed a <c>PriceBand_22</c> frame for the symbol (or
    /// the kill-switch <c>Trading:MarketData:EnablePriceBand</c> is
    /// off) — callers fail open.
    /// </summary>
    bool TryGetBand(string? symbol, out PriceBand band);
}

/// <summary>
/// OPT-E (#487, refs #482 OPT-readiness umbrella). Thread-safe,
/// in-process registry of <see cref="PriceBand"/>s pushed by the
/// upstream SDK <c>PriceBand</c> channel. The host adapter
/// (<c>SdkMarketDataSubscriber</c>) translates each
/// <c>PriceBandEvent</c> the SDK raises — the
/// <see cref="TryProject(decimal?, decimal?, long?, out decimal, out decimal)"/>
/// static helper handles the wire-shape conversion — and calls
/// <see cref="Upsert(string, decimal, decimal, DateTimeOffset)"/>.
/// <para>
/// Concurrency contract is identical to <see cref="SecurityDefinitionRegistry"/>:
/// a single SDK callback thread writes (SDK guarantees ordered,
/// single-threaded event dispatch); hot-path threads read on every
/// pre-trade evaluation. A <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// with <see cref="StringComparer.OrdinalIgnoreCase"/> gives lock-free
/// reads with the same case-insensitive lookup semantics the rest of
/// the trading surface uses.
/// </para>
/// <para>
/// Wholesale-replace semantics: the venue is the source of truth for
/// its own band. Newer Upsert always wins, no merge. This mirrors the
/// "registry-wins" pattern from OPT-D / #494.
/// </para>
/// </summary>
public sealed class PriceBandRegistry : IPriceBandSource
{
    private readonly ConcurrentDictionary<string, PriceBand> _bands =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces (or installs) the band for <paramref name="symbol"/>.
    /// Caller (host adapter) is responsible for translating SBE
    /// discriminators (PRICE_UNIT vs TICKS vs PERCENTAGE) into
    /// absolute lower/upper bounds — see
    /// <see cref="TryProject(decimal?, decimal?, long?, out decimal, out decimal)"/>.
    /// Invalid bounds (lower &gt; upper, non-finite, non-positive) are
    /// dropped so a malformed frame can't poison the registry.
    /// </summary>
    public void Upsert(string symbol, decimal lower, decimal upper, DateTimeOffset asOfUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (lower <= 0m || upper <= 0m) return;
        if (lower > upper) return;
        _bands[symbol] = new PriceBand(lower, upper, asOfUtc);
    }

    /// <inheritdoc/>
    public bool TryGetBand(string? symbol, out PriceBand band)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            band = default;
            return false;
        }
        return _bands.TryGetValue(symbol, out band);
    }

    /// <summary>
    /// Number of symbols currently held by the registry. Exposed for
    /// startup logging and for tests that assert "the SDK frame was
    /// projected" without binding to a specific symbol.
    /// </summary>
    public int Count => _bands.Count;

    /// <summary>
    /// OPT-E translator: side-table for projecting raw SBE primitives
    /// from <c>PriceBand_22</c> into absolute lower/upper bounds.
    /// SDK-agnostic on purpose — the host adapter passes primitives so
    /// the translation can be unit-tested without taking the SDK
    /// package as a test dep.
    /// <para>
    /// Today only <c>PriceLimitType.PRICE_UNIT</c> (FIX-44 value
    /// <c>1</c>) is projected: <paramref name="lowerBand"/> and
    /// <paramref name="upperBand"/> are accepted as absolute prices.
    /// <c>TICKS</c> (<c>2</c>) and <c>PERCENTAGE</c> (<c>3</c>) require
    /// the per-symbol tick / reference price to compute the absolute
    /// envelope and are deliberately dropped here (returns
    /// <c>false</c>) so they can be added in a follow-up without a
    /// breaking change to the registry shape. The
    /// <see cref="Risk.Checks.PriceBandCheck"/> degrades gracefully:
    /// no band ⇒ fail-open with a bypass counter for ops visibility.
    /// </para>
    /// <para>
    /// A null <paramref name="priceLimitType"/> is interpreted as
    /// PRICE_UNIT: real-world B3 dumps in the conformance corpus omit
    /// the discriminator on equities (the bound is always absolute on
    /// the cash market) — dropping those frames wholesale would leave
    /// the equity envelope unguarded.
    /// </para>
    /// </summary>
    public static bool TryProject(
        decimal? lowerBand,
        decimal? upperBand,
        long? priceLimitType,
        out decimal lower,
        out decimal upper)
    {
        lower = default;
        upper = default;
        if (lowerBand is not { } lb || upperBand is not { } ub) return false;
        if (lb <= 0m || ub <= 0m) return false;
        if (lb > ub) return false;

        // SBE PriceLimitType convention shared with upstream
        // pedrosakuma/B3MatchingPlatform#474: 1 = PRICE_UNIT,
        // 2 = TICKS, 3 = PERCENTAGE. Null tolerated as PRICE_UNIT
        // (see XML doc above).
        switch (priceLimitType)
        {
            case null:
            case 1:
                lower = lb;
                upper = ub;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// OPT-E (#487). Default <see cref="IPriceBandSource"/> used when no
/// SDK projection is wired (e.g. unit tests, hosts running with
/// <c>EnablePriceBand=false</c>). Always returns <c>false</c> ⇒
/// <see cref="Risk.Checks.PriceBandCheck"/> becomes a no-op (fail-
/// open). Centralised here so DI registrations and tests share the
/// same null behaviour instead of each rolling their own.
/// </summary>
public sealed class NullPriceBandSource : IPriceBandSource
{
    public static readonly NullPriceBandSource Instance = new();
    private NullPriceBandSource() { }
    public bool TryGetBand(string? symbol, out PriceBand band)
    {
        band = default;
        return false;
    }
}
