using B3.Trading.Application.SubAccount;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #471 (SDK 0.15.0). Pins the domain-string → wire-uint mapping that
/// the gateway uses to stamp <c>TradingSubAccount</c> on every outbound
/// <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c>. The hash itself
/// is an implementation detail (operators can swap a registered
/// lookup-table mapper in via DI), but the contract of the seam
/// — null in / null out, non-null always non-zero, deterministic
/// across processes, firm-scoped, length-extension safe — is the
/// public surface every consumer must rely on.
/// </summary>
public class DeterministicSubAccountWireIdMapperTests
{
    private readonly DeterministicSubAccountWireIdMapper _mapper = new();

    [Fact]
    public void TryMap_NullSubAccount_ReturnsNull()
    {
        Assert.Null(_mapper.TryMap("FIRM-A", subAccountId: null));
    }

    [Fact]
    public void TryMap_NonNullSubAccount_ReturnsNonZero()
    {
        // Every legal input MUST produce a non-zero output so downstream
        // consumers that treat 0 as "unset" don't misclassify a real
        // sub-account as "no sub-account".
        var result = _mapper.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        Assert.NotNull(result);
        Assert.NotEqual(0u, result.Value);
    }

    [Fact]
    public void TryMap_IsDeterministic_AcrossCalls()
    {
        // Same input pair MUST produce the same output across every
        // call — otherwise orders from the same trader would carry
        // different wire ids inside a single session and the venue
        // would see them as different sub-accounts.
        var a = _mapper.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        var b = _mapper.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void TryMap_IsDeterministic_AcrossInstances()
    {
        // Cross-process determinism: a fresh mapper must produce the
        // same output as the existing instance. Pins that the hash has
        // no hidden seed (e.g. process-randomised string.GetHashCode).
        var other = new DeterministicSubAccountWireIdMapper();
        var a = _mapper.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        var b = other.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void TryMap_DifferentSubAccounts_SameFirm_ProduceDifferentIds()
    {
        var a = _mapper.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        var b = _mapper.TryMap("FIRM-A", new SubAccountId("prop"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TryMap_SameSubAccount_DifferentFirms_ProduceDifferentIds()
    {
        // Sub-account ids are namespaced per-firm — FIRM-A:tradingdesk
        // and FIRM-B:tradingdesk are distinct addresses, so the wire
        // ids MUST differ. Otherwise the venue could see the same id
        // for two unrelated traders if the platform ever multiplexes
        // a single session across firms.
        var a = _mapper.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        var b = _mapper.TryMap("FIRM-B", new SubAccountId("tradingdesk"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TryMap_DefendsAgainstLengthExtension()
    {
        // The mapper inserts a zero-byte separator between firmId and
        // subAccountId in the hash input. Without it, ("FIRM01", "A.B")
        // and ("FIRM01A", ".B") would hash identically. Pin that the
        // separator works (the inputs are crafted so they would degenerate
        // to the same byte stream under naive concatenation).
        var a = _mapper.TryMap("FIRM01", new SubAccountId("A.B"));
        var b = _mapper.TryMap("FIRM01A", new SubAccountId(".B"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TryMap_RejectsEmptyFirmId()
    {
        Assert.Throws<ArgumentException>(
            () => _mapper.TryMap("", new SubAccountId("tradingdesk")));
    }

    [Fact]
    public void TryMap_CaseSensitive()
    {
        // SubAccountId is case-sensitive at the domain level; the wire
        // mapper must preserve that distinction so two distinct
        // sub-accounts that differ only in case don't collide.
        var a = _mapper.TryMap("FIRM-A", new SubAccountId("TradingDesk"));
        var b = _mapper.TryMap("FIRM-A", new SubAccountId("tradingdesk"));
        Assert.NotEqual(a, b);
    }
}
