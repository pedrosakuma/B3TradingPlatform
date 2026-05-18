using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// PR #316 P2. Integration coverage for the per-bucket realized-PnL
/// basis on the <see cref="ExecutionReportProcessor"/> live path.
/// Validates the spec contract: "fill in sub-account A increments
/// only A's P&amp;L; master shows sum". A sub-account fill that
/// offsets a position held in the master bucket MUST NOT realise
/// against the master's avg-cost basis — it must realise against
/// the sub-bucket's own basis.
/// </summary>
public class ExecutionReportProcessorSubAccountPnlTests
{
    private const string Firm = "FIRM01";

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        public ConcurrentQueue<(long Seq, WalEvent Event)> Recorded { get; } = new();
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);
        public long Append(WalEvent evt)
        {
            var s = Interlocked.Increment(ref _seq);
            Recorded.Enqueue((s, evt));
            return s;
        }
        public long Append(WalEvent evt, ReadOnlyMemory<byte> _) => Append(evt);
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Bench
    {
        public ExecutionReportProcessor Proc { get; init; } = null!;
        public EventDispatcher Dispatcher { get; init; } = null!;
        public RecordingEventStore Store { get; init; } = null!;
        public PnlKeeper Pnl { get; init; } = null!;
        public SubAccountPnlKeeper SubPnl { get; init; } = null!;
        public OrderOwnershipMap Ownership { get; init; } = null!;
        public WorkingOrderBook Book { get; init; } = null!;
        public PositionKeeper Positions { get; init; } = null!;
        public SubAccountPositionKeeper SubPositions { get; init; } = null!;
        private ulong _nextClOrdId = 1;

        public ulong AddOrder(EndClientId owner, OrderSide side, long qty, decimal px, SubAccountId? sub = null)
        {
            var id = _nextClOrdId++;
            Book.TryAdd(new Order(id, owner, "PETR4", 1UL, side, OrderType.Limit, qty, px,
                firmId: Firm, subAccountId: sub));
            Ownership.Register(id, owner);
            return id;
        }

        public void Fill(ulong clOrdId, long qty, decimal px)
        {
            Dispatcher.Dispatch(
                new ExecutionReportReceivedEvent
                {
                    ClOrdId = clOrdId,
                    ExecKind = nameof(ExecKind.Fill),
                    LeavesQuantity = 0,
                    CumulativeQuantity = qty,
                    LastQuantity = qty,
                    LastPrice = px,
                    Synthetic = false,
                    OrigClOrdId = 0,
                },
                fanOut => Proc.Apply(clOrdId, ExecKind.Fill, 0, qty, qty, px, null, 0, fanOut));
        }
    }

    private static Bench Build()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var subPositions = new SubAccountPositionKeeper();
        var pnl = new PnlKeeper();
        var subPnl = new SubAccountPnlKeeper();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, new NullSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: null,
            feeCalculator: null,
            feeKeeper: null,
            dispatcher: dispatcher,
            pnlKeeper: pnl,
            subAccountPositions: subPositions,
            subAccountPnl: subPnl);
        return new Bench
        {
            Proc = proc,
            Dispatcher = dispatcher,
            Store = store,
            Pnl = pnl,
            SubPnl = subPnl,
            Ownership = ownership,
            Book = book,
            Positions = positions,
            SubPositions = subPositions,
        };
    }

    private static List<RealizedPnlEvent> RealizedEvents(Bench b) =>
        b.Store.Recorded.Select(r => r.Event).OfType<RealizedPnlEvent>().ToList();

    /// <summary>
    /// Spec scenario #1: master seed 200 @ 30; sub-A buys 50 @ 31;
    /// sub-A sells 20 @ 32 → sub-A realized = (32-31)*20 = 20.
    /// CRITICALLY: must NOT realise against master's 30 basis.
    /// </summary>
    [Fact]
    public void SubBucketClose_RealisesAgainstSubBasis_NotMaster()
    {
        var b = Build();
        var owner = new EndClientId("alice");
        var subA = new SubAccountId("subA");
        var day = DateOnly.FromDateTime(DateTime.UtcNow);

        // Master seed: buy 200 @ 30 (no sub-account tag).
        b.Fill(b.AddOrder(owner, OrderSide.Buy, 200, 30m), 200, 30m);
        // Sub-A: buy 50 @ 31 (opens sub-bucket; no realized).
        b.Fill(b.AddOrder(owner, OrderSide.Buy, 50, 31m, subA), 50, 31m);
        // Sub-A: sell 20 @ 32 — closes against SUB basis (31), not master (30).
        b.Fill(b.AddOrder(owner, OrderSide.Sell, 20, 32m, subA), 20, 32m);

        var events = RealizedEvents(b);
        var subEvent = Assert.Single(events, e => e.SubAccountId == "subA");
        Assert.Equal(20m, subEvent.DeltaRealized); // (32 - 31) * 20
        Assert.Equal(20m, b.SubPnl.GetDayRealized(Firm, owner.Value, subA, "PETR4", day));
        // Aggregate per-symbol day-realized equals the sum of the
        // emitted bucket deltas (here just the sub fill).
        Assert.Equal(20m, b.Pnl.GetDayRealized(Firm, owner.Value, "PETR4", day));
    }

    /// <summary>
    /// Spec scenario #2: continuing from #1, master sells 50 @ 35
    /// → master realised = (35-30)*50 = 250. Master basis untouched
    /// by sub activity.
    /// </summary>
    [Fact]
    public void MasterClose_RealisesAgainstMasterOnlyBasis()
    {
        var b = Build();
        var owner = new EndClientId("alice");
        var subA = new SubAccountId("subA");
        var day = DateOnly.FromDateTime(DateTime.UtcNow);

        b.Fill(b.AddOrder(owner, OrderSide.Buy, 200, 30m), 200, 30m);
        b.Fill(b.AddOrder(owner, OrderSide.Buy, 50, 31m, subA), 50, 31m);
        b.Fill(b.AddOrder(owner, OrderSide.Sell, 20, 32m, subA), 20, 32m); // sub realises 20
        // Master sells 50 @ 35 — basis is master-only (30), not aggregate.
        b.Fill(b.AddOrder(owner, OrderSide.Sell, 50, 35m), 50, 35m);

        var events = RealizedEvents(b);
        var masterEvent = Assert.Single(events, e => e.SubAccountId is null);
        Assert.Equal(250m, masterEvent.DeltaRealized); // (35 - 30) * 50

        // Aggregate keeper sums all bucket deltas (master 250 + sub 20 = 270).
        Assert.Equal(270m, b.Pnl.GetDayRealized(Firm, owner.Value, "PETR4", day));
        // Sub bucket is unaffected by master close.
        Assert.Equal(20m, b.SubPnl.GetDayRealized(Firm, owner.Value, subA, "PETR4", day));
    }

    /// <summary>
    /// Spec scenario #3: sub-A sells 20 @ 33 with NO prior buy →
    /// short-from-zero at 33; realized PnL is 0 (opening leg).
    /// Demonstrates that a sub-bucket sell never accidentally
    /// realises against the master's long basis.
    /// </summary>
    [Fact]
    public void SubSell_FromZeroBucket_RealisesZero_EvenWhenMasterIsLong()
    {
        var b = Build();
        var owner = new EndClientId("alice");
        var subA = new SubAccountId("subA");
        var day = DateOnly.FromDateTime(DateTime.UtcNow);

        // Master holds a big long.
        b.Fill(b.AddOrder(owner, OrderSide.Buy, 200, 30m), 200, 30m);
        // Sub-A sells 20 @ 33 (no prior sub buy). Bucket = 0 → short opens.
        b.Fill(b.AddOrder(owner, OrderSide.Sell, 20, 33m, subA), 20, 33m);

        // No realized event emitted (opening leg).
        Assert.Empty(RealizedEvents(b));
        Assert.Equal(0m, b.SubPnl.GetDayRealized(Firm, owner.Value, subA, "PETR4", day));

        // Sub-bucket basis is now (-20 @ 33).
        var basis = b.SubPnl.GetBucketAvgCost(Firm, owner.Value, subA, "PETR4")!;
        Assert.Equal(-20, basis.NetQuantity);
        Assert.Equal(33m, basis.AvgPrice);
    }

    /// <summary>
    /// Spec scenario #4: aggregate position invariant.
    /// Seed master 200 @ 30; master sells 50 @ 35; sub buys 50 @ 31;
    /// sub sells 20 @ 32. Expected: master.qty=150, sub.qty=30,
    /// aggregate=180. Combined with the realized expectations from
    /// scenarios #1 + #2, this is the full pass-7 invariant
    /// (master = liveAggregate − sumSub).
    /// </summary>
    [Fact]
    public void BucketPositionInvariant_HoldsAfterMixedFills()
    {
        var b = Build();
        var owner = new EndClientId("alice");
        var subA = new SubAccountId("subA");

        b.Fill(b.AddOrder(owner, OrderSide.Buy, 200, 30m), 200, 30m);
        b.Fill(b.AddOrder(owner, OrderSide.Sell, 50, 35m), 50, 35m); // master close
        b.Fill(b.AddOrder(owner, OrderSide.Buy, 50, 31m, subA), 50, 31m);
        b.Fill(b.AddOrder(owner, OrderSide.Sell, 20, 32m, subA), 20, 32m);

        var aggPosition = b.Positions.ForEndClientAndFirm(Firm, owner)
            .Single(p => p.Symbol == "PETR4");
        var subPosition = b.SubPositions.ForSubAccount(Firm, owner, subA)
            .Single(p => p.Symbol == "PETR4");
        Assert.Equal(180, aggPosition.NetQuantity); // 200 - 50 + 50 - 20
        Assert.Equal(30, subPosition.NetQuantity);  // 50 - 20
        // master = aggregate − sumSub
        Assert.Equal(150, aggPosition.NetQuantity - subPosition.NetQuantity);
    }

    /// <summary>
    /// PR #316 P2. Snapshot/restore round-trips the per-bucket
    /// avg-cost basis so the first post-restore closing fill on each
    /// bucket realises against its OWN basis (not aggregate / not
    /// zero).
    /// </summary>
    [Fact]
    public void BucketBasis_SnapshotRoundTrip_PreservesPerBucketBasis()
    {
        var k = new SubAccountPnlKeeper();
        var subA = new SubAccountId("subA");
        var subB = new SubAccountId("subB");

        // Master bucket builds 100 @ 30.
        Assert.Equal(0m, k.ApplyBucketFill(Firm, "alice", null, "PETR4", OrderSide.Buy, 100, 30m));
        // Sub-A bucket builds 50 @ 31.
        Assert.Equal(0m, k.ApplyBucketFill(Firm, "alice", subA, "PETR4", OrderSide.Buy, 50, 31m));
        // Sub-B bucket builds short 20 @ 33.
        Assert.Equal(0m, k.ApplyBucketFill(Firm, "alice", subB, "PETR4", OrderSide.Sell, 20, 33m));

        var realized = k.Snapshot();
        var basis = k.SnapshotBasis();
        Assert.Equal(3, basis.Length);

        var k2 = new SubAccountPnlKeeper();
        k2.Restore(realized, basis);

        // Master close after restore → realises against master's 30,
        // not against zero (which would silently miss the realised
        // P&L) nor against an aggregate average that mixed the subs.
        Assert.Equal((32m - 30m) * 40, k2.ApplyBucketFill(Firm, "alice", null, "PETR4", OrderSide.Sell, 40, 32m));
        // Sub-A close after restore → realises against sub-A's 31.
        Assert.Equal((32m - 31m) * 20, k2.ApplyBucketFill(Firm, "alice", subA, "PETR4", OrderSide.Sell, 20, 32m));
        // Sub-B close (short cover) → realises (33 - 30) * 10 = 30.
        Assert.Equal((33m - 30m) * 10, k2.ApplyBucketFill(Firm, "alice", subB, "PETR4", OrderSide.Buy, 10, 30m));
    }

    /// <summary>
    /// PR #316 P2. Legacy snapshot path (no basis block) hydrates to
    /// an empty bucket basis map without throwing — preserves the
    /// pre-PR <c>Restore(snaps)</c> overload behaviour.
    /// </summary>
    [Fact]
    public void Restore_WithoutBasisBlock_LeavesBucketBasisEmpty()
    {
        var k = new SubAccountPnlKeeper();
        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        k.Add(Firm, "alice", new SubAccountId("subA"), "PETR4", day, 42m);

        var k2 = new SubAccountPnlKeeper();
        k2.Restore(k.Snapshot()); // legacy single-arg overload
        Assert.Equal(42m, k2.GetDayRealized(Firm, "alice", new SubAccountId("subA"), "PETR4", day));
        Assert.Null(k2.GetBucketAvgCost(Firm, "alice", new SubAccountId("subA"), "PETR4"));
    }
}
