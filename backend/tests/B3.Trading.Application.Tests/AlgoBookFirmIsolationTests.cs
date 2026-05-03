using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Slice 3: <see cref="AlgoBook"/> is keyed per-firm. Two firms can
/// independently issue the same numeric AlgoId without collision, and
/// per-owner enumerations are scoped to the firm context of the caller.
/// </summary>
public class AlgoBookFirmIsolationTests
{
    [Fact]
    public void TwoFirms_SameAlgoId_CoexistInBook()
    {
        var book = new AlgoBook();
        var alice = new EndClientId("alice");
        var algoA = new Algo(1UL, alice, "FIRM-A", "PETR4", 4321UL,
            OrderSide.Buy, AlgoType.Iceberg, 100, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow);
        var algoB = new Algo(1UL, alice, "FIRM-B", "PETR4", 4321UL,
            OrderSide.Sell, AlgoType.Iceberg, 200, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow);

        Assert.True(book.TryAdd(algoA));
        Assert.True(book.TryAdd(algoB));

        Assert.True(book.TryGet("FIRM-A", 1UL, out var a) && a is not null);
        Assert.True(book.TryGet("FIRM-B", 1UL, out var b) && b is not null);
        Assert.Equal(OrderSide.Buy, a!.Side);
        Assert.Equal(OrderSide.Sell, b!.Side);
    }

    [Fact]
    public void EnumerateForOwner_IsFirmScoped()
    {
        var book = new AlgoBook();
        var alice = new EndClientId("alice");
        book.TryAdd(new Algo(1UL, alice, "FIRM-A", "PETR4", 4321UL,
            OrderSide.Buy, AlgoType.Iceberg, 100, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow));
        book.TryAdd(new Algo(2UL, alice, "FIRM-A", "VALE3", 1234UL,
            OrderSide.Buy, AlgoType.Iceberg, 100, new IcebergParameters(10, 50m), DateTimeOffset.UtcNow));
        book.TryAdd(new Algo(1UL, alice, "FIRM-B", "PETR4", 4321UL,
            OrderSide.Sell, AlgoType.Iceberg, 100, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow));

        Assert.Equal(2, book.EnumerateForOwner("FIRM-A", alice).Count);
        Assert.Single(book.EnumerateForOwner("FIRM-B", alice));
    }

    [Fact]
    public void TryAdd_DuplicateForSameFirm_ReturnsFalse()
    {
        var book = new AlgoBook();
        var alice = new EndClientId("alice");
        var algo = new Algo(1UL, alice, "FIRM-A", "PETR4", 4321UL,
            OrderSide.Buy, AlgoType.Iceberg, 100, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow);
        Assert.True(book.TryAdd(algo));
        var dup = new Algo(1UL, alice, "FIRM-A", "VALE3", 1234UL,
            OrderSide.Buy, AlgoType.Iceberg, 100, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow);
        Assert.False(book.TryAdd(dup));
    }
}
