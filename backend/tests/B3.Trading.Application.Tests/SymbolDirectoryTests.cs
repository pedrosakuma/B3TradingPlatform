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
}
