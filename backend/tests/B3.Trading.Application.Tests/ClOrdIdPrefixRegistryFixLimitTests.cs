using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #440. Defense-in-depth coverage for the FIX 4.4 §3.1.1 20-char
/// ClOrdID limit. The bit layout in <see cref="ClOrdIdPrefixRegistry"/>
/// (prefix 21 bits, counter 40 bits) currently caps the produced
/// <c>ulong</c> at <c>2^61 - 1 = 2_305_843_009_213_693_951</c> (19
/// decimal digits) so the venue limit can never be hit today — but a
/// future encoding change must NOT regress that silently. These
/// tests pin both the canonical wire format and the runtime guard.
/// </summary>
public class ClOrdIdPrefixRegistryFixLimitTests
{
    [Fact]
    public void MaxFixClOrdIdLength_MatchesFixSpec()
    {
        Assert.Equal(20, ClOrdIdPrefixRegistry.MaxFixClOrdIdLength);
    }

    [Fact]
    public void EncodeFixClOrdId_AtBitLayoutMax_FitsWithin20Chars()
    {
        // Maximum value the current layout can produce:
        // prefixIdx = MaxPrefixIndex - 1, counter = CounterMask.
        var prefixIdx = (ulong)(ClOrdIdPrefixRegistry.MaxPrefixIndex - 1);
        var maxValue = (prefixIdx << ClOrdIdPrefixRegistry.CounterBits) | ClOrdIdPrefixRegistry.CounterMask;

        var encoded = ClOrdIdPrefixRegistry.EncodeFixClOrdId(maxValue);

        // Sanity: the encoding is the invariant-culture decimal string.
        Assert.Equal(maxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), encoded);

        Assert.True(encoded.Length <= ClOrdIdPrefixRegistry.MaxFixClOrdIdLength,
            $"Encoded ClOrdID at bit-layout max is {encoded.Length} chars ('{encoded}'), exceeds FIX limit {ClOrdIdPrefixRegistry.MaxFixClOrdIdLength}.");

        // Pin today's headroom so a regression that pushes the layout
        // toward the limit triggers a code review, not a venue reject.
        Assert.Equal(19, encoded.Length);
    }

    [Fact]
    public void EncodeFixClOrdId_AtUlongMax_StillFitsWithin20Chars()
    {
        // The ulong type itself caps at 20 decimal digits
        // (ulong.MaxValue = 18446744073709551615). Sanity-check this
        // independently of our bit layout so a future encoding that
        // emits the raw ulong without our prefix/counter split still
        // satisfies the FIX cap.
        var encoded = ClOrdIdPrefixRegistry.EncodeFixClOrdId(ulong.MaxValue);
        Assert.Equal(20, encoded.Length);
        Assert.True(encoded.Length <= ClOrdIdPrefixRegistry.MaxFixClOrdIdLength);
    }

    [Fact]
    public void Generate_ProducesEncodingWithin20Chars()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var endClient = new EndClientId("END-001");

        for (var i = 0; i < 100; i++)
        {
            var id = registry.Generate(endClient);
            var encoded = ClOrdIdPrefixRegistry.EncodeFixClOrdId(id);
            Assert.True(encoded.Length <= ClOrdIdPrefixRegistry.MaxFixClOrdIdLength,
                $"Generated ClOrdID '{encoded}' is {encoded.Length} chars, exceeds FIX limit.");
        }
    }
}
