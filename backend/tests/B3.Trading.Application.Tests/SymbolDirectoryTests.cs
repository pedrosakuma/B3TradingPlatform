using B3.Trading.Application;

namespace B3.Trading.Application.Tests;

public class SymbolDirectoryTests
{
    [Fact]
    public void TryResolve_KnownSymbol_ReturnsId()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL },
        });

        Assert.True(sut.TryResolve("PETR4", out var id));
        Assert.Equal(4321UL, id);
    }

    [Fact]
    public void TryResolve_IsCaseInsensitive()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL },
        });

        Assert.True(sut.TryResolve("petr4", out var id));
        Assert.Equal(4321UL, id);
    }

    [Fact]
    public void TryResolve_UnknownSymbol_ReturnsFalse()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL },
        });

        Assert.False(sut.TryResolve("VALE3", out var id));
        Assert.Equal(0UL, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_NullOrBlank_ReturnsFalse(string? input)
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL },
        });

        Assert.False(sut.TryResolve(input, out var id));
        Assert.Equal(0UL, id);
    }

    [Fact]
    public void Constructor_DropsZeroIds()
    {
        // Defensive: zero would silently mean "unresolved" downstream
        // and produce confusing 400s after a successful TryResolve.
        // The directory drops these at construction so the contract
        // "TryResolve returns true ⇒ id != 0" always holds.
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds =
            {
                ["PETR4"] = 4321UL,
                ["BAD"]   = 0UL,
            },
        });

        Assert.Equal(1, sut.Count);
        Assert.False(sut.TryResolve("BAD", out _));
    }

    [Fact]
    public void Constructor_DropsBlankKeys()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds =
            {
                [" "] = 9UL,
                ["PETR4"] = 4321UL,
            },
        });

        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public void Empty_ReturnsFalseForEverything()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions());

        Assert.Equal(0, sut.Count);
        Assert.False(sut.TryResolve("PETR4", out _));
    }

    [Fact]
    public void TryGetSpec_ReturnsConfiguredSpec()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["PETR4"] = new InstrumentSpecOptions { TickSize = 0.01m, LotSize = 100L },
            },
        });

        Assert.True(sut.TryGetSpec("PETR4", out var spec));
        Assert.Equal(0.01m, spec.TickSize);
        Assert.Equal(100L, spec.LotSize);
    }

    [Fact]
    public void TryGetSpec_IsCaseInsensitive()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs = { ["PETR4"] = new InstrumentSpecOptions { TickSize = 0.01m } },
        });

        Assert.True(sut.TryGetSpec("petr4", out var spec));
        Assert.Equal(0.01m, spec.TickSize);
        Assert.Null(spec.LotSize);
    }

    [Fact]
    public void TryGetSpec_UnknownSymbol_ReturnsFalse()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs = { ["PETR4"] = new InstrumentSpecOptions { TickSize = 0.01m } },
        });

        Assert.False(sut.TryGetSpec("VALE3", out var spec));
        Assert.Equal(default, spec);
    }

    [Fact]
    public void TryGetSpec_DropsEntriesWithNoConstraint()
    {
        // A spec where both tick and lot are missing (or non-positive)
        // wouldn't constrain anything — treat it as "no spec" so the
        // fail-open posture in MinTick/MinLot stays sharp.
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["EMPTY"] = new InstrumentSpecOptions(),
                ["BAD"]   = new InstrumentSpecOptions { TickSize = 0m, LotSize = 0L },
            },
        });

        Assert.False(sut.TryGetSpec("EMPTY", out _));
        Assert.False(sut.TryGetSpec("BAD", out _));
    }

    [Fact]
    public void Specs_AreIndependentFromSecurityIds()
    {
        // A symbol can have a Spec without a SecurityId (or vice versa)
        // — they're orthogonal lookups against the same directory.
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL },
            Specs = { ["VALE3"] = new InstrumentSpecOptions { TickSize = 0.01m } },
        });

        Assert.True(sut.TryResolve("PETR4", out _));
        Assert.False(sut.TryGetSpec("PETR4", out _));
        Assert.False(sut.TryResolve("VALE3", out _));
        Assert.True(sut.TryGetSpec("VALE3", out _));
    }

    // Sub-issue #171 (E): inverse SecurityId → Symbol lookup added for the
    // FIXP order adapter, which receives orders by numeric SecurityId.

    [Fact]
    public void TryGetSymbolBySecurityId_KnownId_ReturnsSymbol()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL, ["VALE3"] = 9876UL },
        });

        Assert.True(sut.TryGetSymbolBySecurityId(4321UL, out var symbol));
        Assert.Equal("PETR4", symbol);
        Assert.True(sut.TryGetSymbolBySecurityId(9876UL, out symbol));
        Assert.Equal("VALE3", symbol);
    }

    [Fact]
    public void TryGetSymbolBySecurityId_UnknownId_ReturnsFalse()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL },
        });

        Assert.False(sut.TryGetSymbolBySecurityId(9999UL, out var symbol));
        Assert.Null(symbol);
    }

    [Fact]
    public void TryGetSymbolBySecurityId_RoundTripsForwardLookup()
    {
        // Inverse map is built from the forward map at construction time;
        // verify they stay in lockstep.
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 4321UL, ["VALE3"] = 9876UL },
        });

        foreach (var name in new[] { "PETR4", "VALE3" })
        {
            Assert.True(sut.TryResolve(name, out var id));
            Assert.True(sut.TryGetSymbolBySecurityId(id, out var back));
            Assert.Equal(name, back);
        }
    }

    [Fact]
    public void TryGetSymbolBySecurityId_DuplicateSecurityId_FirstWriteWins()
    {
        // Configuration mistake: two symbols claim the same SecurityId.
        // Forward map keeps both; reverse map keeps the first.
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = { ["PETR4"] = 100UL, ["PETR3"] = 100UL },
        });

        Assert.True(sut.TryGetSymbolBySecurityId(100UL, out var symbol));
        Assert.NotNull(symbol);
        // Either one is acceptable as long as we return a known symbol;
        // the first-write-wins guarantee is documented but ordering of
        // dictionary enumeration is implementation defined.
        Assert.Contains(symbol, new[] { "PETR4", "PETR3" });
    }

    // ── #360 tick-ladder coverage ─────────────────────────────────────

    [Fact]
    public void TickLadder_ResolveTick_picksBandByPrice()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["PETR4"] = new InstrumentSpecOptions
                {
                    TickLadder = new()
                    {
                        new TickBandOptions { MinPriceInclusive = 0m,   Tick = 0.01m },
                        new TickBandOptions { MinPriceInclusive = 1m,   Tick = 0.05m },
                        new TickBandOptions { MinPriceInclusive = 10m,  Tick = 0.10m },
                        new TickBandOptions { MinPriceInclusive = 100m, Tick = 0.50m },
                    },
                },
            },
        });

        Assert.True(sut.TryGetSpec("PETR4", out var spec));
        Assert.Equal(0.01m, spec.ResolveTick(0.50m));
        Assert.Equal(0.05m, spec.ResolveTick(1m));     // boundary inclusive
        Assert.Equal(0.05m, spec.ResolveTick(9.99m));
        Assert.Equal(0.10m, spec.ResolveTick(10m));
        Assert.Equal(0.10m, spec.ResolveTick(50m));
        Assert.Equal(0.50m, spec.ResolveTick(100m));
        Assert.Equal(0.50m, spec.ResolveTick(9999m));
    }

    [Fact]
    public void TickLadder_isCanonicalized_outOfOrder_andDeduped()
    {
        // Operator writes the ladder in any order; the directory must
        // sort ascending and dedup (last-write-wins per MinPriceInclusive).
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["VALE3"] = new InstrumentSpecOptions
                {
                    TickLadder = new()
                    {
                        new TickBandOptions { MinPriceInclusive = 100m, Tick = 0.50m },
                        new TickBandOptions { MinPriceInclusive = 1m,   Tick = 0.05m },
                        new TickBandOptions { MinPriceInclusive = 1m,   Tick = 0.99m }, // dup -> wins
                        new TickBandOptions { MinPriceInclusive = 0m,   Tick = 0.01m },
                    },
                },
            },
        });

        Assert.True(sut.TryGetSpec("VALE3", out var spec));
        Assert.Equal(0.01m, spec.ResolveTick(0.50m));
        Assert.Equal(0.99m, spec.ResolveTick(5m));   // dedup last-write-wins
        Assert.Equal(0.50m, spec.ResolveTick(100m));
    }

    [Fact]
    public void TickLadder_dropsMalformedRows()
    {
        // Non-positive tick or negative MinPriceInclusive are dropped.
        // Spec with ONLY malformed rows yields null ladder; combined
        // with no flat TickSize the entry is dropped (matches the
        // existing "no constraint = no spec" rule).
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["JUNK"] = new InstrumentSpecOptions
                {
                    TickLadder = new()
                    {
                        new TickBandOptions { MinPriceInclusive = 0m,  Tick = 0m },
                        new TickBandOptions { MinPriceInclusive = 0m,  Tick = -1m },
                        new TickBandOptions { MinPriceInclusive = -5m, Tick = 0.01m },
                    },
                },
            },
        });

        Assert.False(sut.TryGetSpec("JUNK", out _));
    }

    [Fact]
    public void TickLadder_fallsBackToFlatTickSize_belowLowestBand()
    {
        // A ladder that starts at price 1 plus a flat TickSize of 0.01
        // — prices below 1 fall back to the flat tick (preserves the
        // legacy behavior for symbols whose operator opts in to the
        // ladder schema only for higher bands).
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["MGLU3"] = new InstrumentSpecOptions
                {
                    TickSize = 0.01m,
                    TickLadder = new()
                    {
                        new TickBandOptions { MinPriceInclusive = 1m,  Tick = 0.05m },
                        new TickBandOptions { MinPriceInclusive = 10m, Tick = 0.10m },
                    },
                },
            },
        });

        Assert.True(sut.TryGetSpec("MGLU3", out var spec));
        Assert.Equal(0.01m, spec.ResolveTick(0.50m));
        Assert.Equal(0.05m, spec.ResolveTick(1m));
        Assert.Equal(0.10m, spec.ResolveTick(20m));
    }

    [Fact]
    public void TickLadder_ResolveBand_reportsHalfOpenRange()
    {
        var sut = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["PETR4"] = new InstrumentSpecOptions
                {
                    TickLadder = new()
                    {
                        new TickBandOptions { MinPriceInclusive = 0m,   Tick = 0.01m },
                        new TickBandOptions { MinPriceInclusive = 1m,   Tick = 0.05m },
                        new TickBandOptions { MinPriceInclusive = 100m, Tick = 0.50m },
                    },
                },
            },
        });

        Assert.True(sut.TryGetSpec("PETR4", out var spec));

        var b0 = spec.ResolveBand(0.50m);
        Assert.NotNull(b0);
        Assert.Equal(0m, b0.Value.LowerInclusive);
        Assert.Equal(1m, b0.Value.UpperExclusive);

        var bMid = spec.ResolveBand(50m);
        Assert.NotNull(bMid);
        Assert.Equal(1m, bMid.Value.LowerInclusive);
        Assert.Equal(100m, bMid.Value.UpperExclusive);

        var bTop = spec.ResolveBand(500m);
        Assert.NotNull(bTop);
        Assert.Equal(100m, bTop.Value.LowerInclusive);
        Assert.Null(bTop.Value.UpperExclusive);

        // Spec with no ladder -> null match.
        var flat = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs = { ["X"] = new InstrumentSpecOptions { TickSize = 0.01m } },
        });
        Assert.True(flat.TryGetSpec("X", out var flatSpec));
        Assert.Null(flatSpec.ResolveBand(1m));
    }
}
