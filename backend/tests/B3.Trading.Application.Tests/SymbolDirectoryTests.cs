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
}
