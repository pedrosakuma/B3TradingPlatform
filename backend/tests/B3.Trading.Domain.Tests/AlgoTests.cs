using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

public class AlgoTests
{
    private static readonly EndClientId Alice = new("alice");
    private const string Firm = "TEST";

    private static Algo NewIceberg(ulong id = 1, long total = 1000, long display = 100, decimal? limit = 30m) =>
        new(id, Alice, Firm, "PETR4", 4321UL, OrderSide.Buy, AlgoType.Iceberg,
            total, new IcebergParameters(display, limit), DateTimeOffset.UtcNow);

    private static Algo NewTwap(ulong id = 1, long total = 1000, int slices = 10) =>
        new(id, Alice, Firm, "PETR4", 4321UL, OrderSide.Buy, AlgoType.Twap,
            total,
            new TwapParameters(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10), slices, OrderType.Market, null),
            DateTimeOffset.UtcNow);

    [Fact]
    public void Construction_AssignsFieldsAndStartsPendingNew()
    {
        var algo = NewIceberg();
        Assert.Equal(AlgoStatus.PendingNew, algo.Status);
        Assert.Equal(AlgoTerminalReason.None, algo.TerminalReason);
        Assert.Equal(1000, algo.RemainingQuantity);
        Assert.False(algo.IsTerminal);
    }

    [Fact]
    public void Construction_RejectsZeroAlgoId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Algo(0, Alice, Firm, "PETR4", 4321UL, OrderSide.Buy, AlgoType.Iceberg, 100,
                new IcebergParameters(10, 30m), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Construction_RejectsParametersOfWrongType()
    {
        // TwapParameters with AlgoType.Iceberg is a programming error.
        var twap = new TwapParameters(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), 5, OrderType.Limit, 30m);
        Assert.Throws<ArgumentException>(() =>
            new Algo(1, Alice, Firm, "PETR4", 4321UL, OrderSide.Buy, AlgoType.Iceberg, 100,
                twap, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkWorking_AdvancesOnceAndIsIdempotent()
    {
        var algo = NewIceberg();
        algo.MarkWorking();
        Assert.Equal(AlgoStatus.Working, algo.Status);
        algo.MarkWorking();
        Assert.Equal(AlgoStatus.Working, algo.Status);
    }

    [Fact]
    public void RecordFill_AccumulatesAndUpdatesRemaining()
    {
        var algo = NewIceberg(total: 500);
        algo.RecordFill(120);
        algo.RecordFill(80);
        Assert.Equal(200, algo.FilledQuantity);
        Assert.Equal(300, algo.RemainingQuantity);
    }

    [Fact]
    public void RequestCancel_TransitionsAnyNonTerminalToCancelling()
    {
        var algo = NewIceberg();
        algo.MarkWorking();
        algo.RequestCancel();
        Assert.Equal(AlgoStatus.Cancelling, algo.Status);
        // Idempotent — operator can spam DELETE.
        algo.RequestCancel();
        Assert.Equal(AlgoStatus.Cancelling, algo.Status);
    }

    [Fact]
    public void RequestCancel_IsNoOpAfterTerminal()
    {
        var algo = NewIceberg();
        algo.MarkWorking();
        algo.RecordTerminal(AlgoStatus.Completed, AlgoTerminalReason.None, DateTimeOffset.UtcNow);
        algo.RequestCancel();
        Assert.Equal(AlgoStatus.Completed, algo.Status);
    }

    [Theory]
    [InlineData(AlgoStatus.Completed, AlgoTerminalReason.None)]
    [InlineData(AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled)]
    [InlineData(AlgoStatus.Suspended, AlgoTerminalReason.RiskRejected)]
    [InlineData(AlgoStatus.Expired, AlgoTerminalReason.TwapWindowExpired)]
    public void RecordTerminal_SetsStatusReasonAndTimestamp(AlgoStatus status, AlgoTerminalReason reason)
    {
        var algo = NewTwap();
        var at = DateTimeOffset.UtcNow;
        algo.RecordTerminal(status, reason, at);
        Assert.Equal(status, algo.Status);
        Assert.Equal(reason, algo.TerminalReason);
        Assert.Equal(at, algo.TerminalAtUtc);
        Assert.True(algo.IsTerminal);
    }

    [Fact]
    public void RecordTerminal_RejectsNonTerminalStatus()
    {
        var algo = NewIceberg();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            algo.RecordTerminal(AlgoStatus.Working, AlgoTerminalReason.None, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RecordTerminal_RejectsConflictingReentry()
    {
        var algo = NewIceberg();
        algo.RecordTerminal(AlgoStatus.Completed, AlgoTerminalReason.None, DateTimeOffset.UtcNow);
        // Replay-safe: same status is no-op.
        algo.RecordTerminal(AlgoStatus.Completed, AlgoTerminalReason.None, DateTimeOffset.UtcNow);
        // Different terminal must throw — would indicate a logic bug.
        Assert.Throws<InvalidOperationException>(() =>
            algo.RecordTerminal(AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled, DateTimeOffset.UtcNow));
    }
}
