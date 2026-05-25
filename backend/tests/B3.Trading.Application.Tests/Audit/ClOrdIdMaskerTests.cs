using B3.Trading.Application.Audit;
using Microsoft.Extensions.Options;
using Xunit;

namespace B3.Trading.Application.Tests.Audit;

/// <summary>
/// #435 Part B. Contract tests for <see cref="ClOrdIdMasker"/>:
/// determinism within (firm, UTC-day), cross-day unlinkability,
/// cross-firm isolation, ClOrdId/AlgoId domain separation, and the
/// boot-guard on unset salt.
/// </summary>
public sealed class ClOrdIdMaskerTests
{
    private static ClOrdIdMasker MakerAt(DateTime utcNow, string salt = ClOrdIdMaskerOptions.TestOnlySalt) =>
        new(new ClOrdIdMaskerOptions { ClOrdIdMaskSalt = salt }, () => utcNow);

    [Fact]
    public void Ctor_Throws_WhenSaltUnset()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ClOrdIdMasker(Options.Create(new ClOrdIdMaskerOptions())));
        Assert.Contains("ClOrdIdMaskSalt", ex.Message);
    }

    [Fact]
    public void Ctor_Accepts_TestOnlySalt()
    {
        _ = new ClOrdIdMasker(Options.Create(new ClOrdIdMaskerOptions
        {
            ClOrdIdMaskSalt = ClOrdIdMaskerOptions.TestOnlySalt,
        }));
    }

    [Fact]
    public void Mask_IsDeterministic_WithinSameDay()
    {
        var m = MakerAt(new DateTime(2026, 5, 25, 12, 30, 0, DateTimeKind.Utc));
        var a = m.MaskClOrdId("firmA", 12345UL);
        var b = m.MaskClOrdId("firmA", 12345UL);
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void Mask_Rotates_AcrossUtcDays()
    {
        var day1 = MakerAt(new DateTime(2026, 5, 25, 23, 59, 0, DateTimeKind.Utc));
        var day2 = MakerAt(new DateTime(2026, 5, 26, 00, 01, 0, DateTimeKind.Utc));
        Assert.NotEqual(day1.MaskClOrdId("firmA", 12345UL), day2.MaskClOrdId("firmA", 12345UL));
    }

    [Fact]
    public void Mask_IsolatesAcrossFirms()
    {
        var m = MakerAt(new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(m.MaskClOrdId("firmA", 42UL), m.MaskClOrdId("firmB", 42UL));
    }

    [Fact]
    public void Mask_SeparatesClOrdIdVsAlgoIdDomain()
    {
        // Same firm, same day, same numeric id — but different domain
        // (ClOrdId vs ParentAlgoId) must produce different opaque tokens
        // so a coincidental id collision does not leak across the two
        // identifier namespaces on the drop-copy stream.
        var m = MakerAt(new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(m.MaskClOrdId("firmA", 99UL), m.MaskAlgoId("firmA", 99UL));
    }

    [Fact]
    public void Mask_DiffersAcrossSequentialIds()
    {
        // Drop-copy threat model: 100 sequential child ClOrdIds must
        // hash to 100 distinct opaque tokens — otherwise a counterparty
        // could group consecutive children of the same algo by mask
        // collisions even with daily rotation in place.
        var m = MakerAt(new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong i = 1; i <= 100; i++)
        {
            Assert.True(seen.Add(m.MaskClOrdId("firmA", i)), $"collision at id={i}");
        }
    }

    [Fact]
    public void Mask_DifferentSalts_DifferentOutputs()
    {
        var t = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        var a = MakerAt(t, "salt-A").MaskClOrdId("firmA", 1UL);
        var b = MakerAt(t, "salt-B").MaskClOrdId("firmA", 1UL);
        Assert.NotEqual(a, b);
    }
}
