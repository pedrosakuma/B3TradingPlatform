using B3.Trading.Application;
using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// OPT-D (#486, refs #454 Fase 2). When the directory is constructed
/// with a <see cref="SecurityDefinitionRegistry"/> overlay,
/// <see cref="SymbolDirectory.TryGetSpec(string?, out InstrumentSpec)"/>
/// must consult the registry FIRST and only fall back to the
/// operator-configured static dictionary on miss. The static-only
/// ctor preserves the v1 behaviour (registry-less; tests that build
/// the directory directly remain byte-identical).
/// </summary>
public class SymbolDirectoryRegistryOverlayTests
{
    private static SymbolDirectoryOptions OneSpec(string symbol, decimal tick, long lot) =>
        new()
        {
            SecurityIds = { [symbol] = 4 },
            Specs =
            {
                [symbol] = new InstrumentSpecOptions { TickSize = tick, LotSize = lot },
            },
        };

    [Fact]
    public void TryGetSpec_FallsBackToConfig_WhenRegistryEmpty()
    {
        var registry = new SecurityDefinitionRegistry();
        var dir = new SymbolDirectory(OneSpec("PETR4", 0.01m, 100), registry);

        Assert.True(dir.TryGetSpec("PETR4", out var spec));
        Assert.Equal(0.01m, spec.TickSize);
        Assert.Equal(100, spec.LotSize);
    }

    [Fact]
    public void TryGetSpec_RegistryWins_OverConfig()
    {
        var registry = new SecurityDefinitionRegistry();
        // Config says tick=0.01 / lot=100; SDK frame says tick=0.05 /
        // lot=10. Registry-wins semantics: venue truth replaces the
        // hand-typed YAML wholesale (no field-level merge).
        registry.Upsert("PETR4", new InstrumentSpec(TickSize: 0.05m, LotSize: 10), 4);
        var dir = new SymbolDirectory(OneSpec("PETR4", 0.01m, 100), registry);

        Assert.True(dir.TryGetSpec("PETR4", out var spec));
        Assert.Equal(0.05m, spec.TickSize);
        Assert.Equal(10, spec.LotSize);
    }

    [Fact]
    public void TryGetSpec_RegistryWins_EvenWhenRegistryHasOnlySomeFields()
    {
        // Even if the registry frame only carries tick (no lot, no
        // option), it still wins as a whole. This is intentional —
        // a partial SDK frame implies the venue's authoritative
        // statement is "tick T, no other constraint". Merging field-
        // by-field would silently keep stale operator overrides.
        var registry = new SecurityDefinitionRegistry();
        registry.Upsert("PETR4", new InstrumentSpec(TickSize: 0.05m, LotSize: null), 4);
        var dir = new SymbolDirectory(OneSpec("PETR4", 0.01m, 100), registry);

        Assert.True(dir.TryGetSpec("PETR4", out var spec));
        Assert.Equal(0.05m, spec.TickSize);
        Assert.Null(spec.LotSize);
    }

    [Fact]
    public void TryGetSpec_NoRegistry_BehavesAsV1()
    {
        // Backward-compat: the single-arg ctor (no registry) must
        // continue to return the config-bound spec unchanged.
        var dir = new SymbolDirectory(OneSpec("PETR4", 0.01m, 100));
        Assert.True(dir.TryGetSpec("PETR4", out var spec));
        Assert.Equal(0.01m, spec.TickSize);
        Assert.Equal(100, spec.LotSize);
    }

    [Fact]
    public void TryGetSpec_RegistryMiss_ConfigMiss_ReturnsFalse()
    {
        var registry = new SecurityDefinitionRegistry();
        var dir = new SymbolDirectory(new SymbolDirectoryOptions(), registry);
        Assert.False(dir.TryGetSpec("UNKNOWN", out _));
    }

    [Fact]
    public void TryResolve_IsNotAffectedByRegistry()
    {
        // SecurityId resolution is still config-only on this PR — the
        // registry exposes TryGetSecurityId for future wiring but the
        // SymbolDirectory.TryResolve path stays bound to the static
        // dictionary. Lock that in so a future overlay doesn't break
        // the FIXP adapter's lookup.
        var registry = new SecurityDefinitionRegistry();
        registry.Upsert("DYNAMIC", new InstrumentSpec(0.01m, 100), 999);
        var dir = new SymbolDirectory(OneSpec("PETR4", 0.01m, 100), registry);

        Assert.True(dir.TryResolve("PETR4", out var id));
        Assert.Equal(4UL, id);
        Assert.False(dir.TryResolve("DYNAMIC", out var dyn));
        Assert.Equal(0UL, dyn);
    }
}
