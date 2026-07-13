using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #435 Part B. Verifies that the new <c>ToDropCopyDto</c> projection
/// masks ClOrdId + ParentAlgoId and strips AlgoSliceSeq, while the
/// legacy <c>ToDto</c> projection (still used by per-user
/// <c>orders.me</c> / <c>executions.me</c>) is left untouched.
/// </summary>
public sealed class DropCopyDtoMaskingTests
{
    private static readonly EndClientId Alice = new("alice");
    private const string Firm = "FIRM01";

    private static ClOrdIdMasker MakeMasker(DateTime? at = null) =>
        new(
            new ClOrdIdMaskerOptions { ClOrdIdMaskSalt = ClOrdIdMaskerOptions.TestOnlySalt },
            () => at ?? new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void OrderToDropCopyDto_MasksClOrdIdAndParentAlgoId_AndStripsSliceSeq()
    {
        var masker = MakeMasker();
        var order = new Order(
            clOrdId: 12345UL, owner: Alice, symbol: "PETR4", securityId: 9001UL,
            side: OrderSide.Buy, type: OrderType.Limit, quantity: 100, price: 30m,
            firmId: Firm, parentAlgoId: 999UL, algoSliceSeq: 7);

        var raw = order.ToDto();
        var masked = order.ToDropCopyDto(masker, Firm);

        // Raw projection (orders.me) is unchanged.
        Assert.Equal("12345", raw.ClOrdId);
        Assert.Equal("999", raw.ParentAlgoId);
        Assert.Equal(7, raw.AlgoSliceSeq);

        // Drop-copy projection masks both ids + strips slice seq.
        Assert.NotEqual("12345", masked.ClOrdId);
        Assert.NotEqual("999", masked.ParentAlgoId);
        Assert.Equal(16, masked.ClOrdId.Length);
        Assert.Equal(16, masked.ParentAlgoId!.Length);
        Assert.Null(masked.AlgoSliceSeq);

        // Non-identity fields are passed through verbatim.
        Assert.Equal(order.Symbol, masked.Symbol);
        Assert.Equal(order.Quantity, masked.Quantity);
        Assert.Equal(order.Price, masked.Price);
    }

    [Fact]
    public void OrderToDropCopyDto_NullParentAlgoId_StaysNull()
    {
        var masker = MakeMasker();
        var order = new Order(
            clOrdId: 42UL, owner: Alice, symbol: "PETR4", securityId: 9001UL,
            side: OrderSide.Buy, type: OrderType.Limit, quantity: 100, price: 30m,
            firmId: Firm);

        var masked = order.ToDropCopyDto(masker, Firm);
        Assert.Null(masked.ParentAlgoId);
        Assert.Null(masked.AlgoSliceSeq);
        Assert.NotEqual("42", masked.ClOrdId);
    }

    [Fact]
    public void ExecutionEventToDropCopyDto_MasksClOrdId()
    {
        var masker = MakeMasker();
        var ev = new ExecutionEvent(
            Owner: Alice, ClOrdId: 99UL, Symbol: "PETR4",
            Side: OrderSide.Buy, Status: OrderStatus.PartiallyFilled, Kind: ExecKind.PartialFill,
            LeavesQuantity: 50, CumulativeQuantity: 50, LastQuantity: 50, LastPrice: 30m,
            RejectReason: null, TimestampUtc: DateTimeOffset.UtcNow,
            FirmId: Firm);

        var raw = ev.ToDto();
        var masked = ev.ToDropCopyDto(masker, Firm);

        Assert.Equal("99", raw.ClOrdId);
        Assert.NotEqual("99", masked.ClOrdId);
        Assert.Equal(16, masked.ClOrdId.Length);
        Assert.Equal(ev.LastQuantity, masked.LastQuantity);
        Assert.Equal(ev.LastPrice, masked.LastPrice);
    }

    [Fact]
    public void Entropy_100SequentialChildren_AllDistinctMasks()
    {
        // Drop-copy threat model — see ClOrdIdMaskerTests; reasserted
        // at the DTO projection layer to catch any future short-circuit
        // that might leak the raw id past the masker.
        var masker = MakeMasker();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong i = 1; i <= 100; i++)
        {
            var o = new Order(
                clOrdId: i, owner: Alice, symbol: "PETR4", securityId: 9001UL,
                side: OrderSide.Buy, type: OrderType.Limit, quantity: 100, price: 30m,
                firmId: Firm);
            Assert.True(seen.Add(o.ToDropCopyDto(masker, Firm).ClOrdId));
        }
    }
}
