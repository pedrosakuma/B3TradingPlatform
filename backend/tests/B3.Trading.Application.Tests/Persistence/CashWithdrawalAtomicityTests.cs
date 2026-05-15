using System.Runtime.CompilerServices;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Q2.2 (#269) P1 regression. Pins the atomicity contract for cash
/// withdrawals: the keeper debit and the WAL append must execute under
/// the same lock the snapshot service takes. Otherwise a snapshot can
/// interleave between TryWithdraw and Append, persisting a reduced
/// balance with no matching event in the WAL — permanent cash loss on
/// restore.
/// </summary>
public class CashWithdrawalAtomicityTests : IDisposable
{
    private readonly string _root;

    public CashWithdrawalAtomicityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-cashatom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private PersistenceOptions Opts() => new()
    {
        DataDirectory = _root,
        FirmId = "test",
        ChannelCapacity = 1024,
        GroupCommitMaxRecords = 8,
        GroupCommitWindow = TimeSpan.FromMilliseconds(5),
        FsyncOnFlush = false,
    };

    /// <summary>
    /// Direct regression for the #276 P1: a snapshot fired concurrently
    /// with a withdrawal must not be able to capture a "debited but not
    /// appended" state. We force the issue by wedging the WAL Append
    /// inside the dispatcher critical section: a side thread fires
    /// WithSnapshotLock during the wedge. With the old code, TryWithdraw
    /// ran outside the lock and the side thread could capture the
    /// debited balance with the matching event still missing from the
    /// WAL. With the fix, the side thread is blocked on the dispatcher
    /// lock until the append completes, so any captured snapshot
    /// state is consistent with the WAL.
    /// </summary>
    [Fact]
    public void Withdrawal_SnapshotCannotInterleaveBetweenDebitAndAppend()
    {
        var store = new WedgeableStore();
        var dispatcher = new EventDispatcher(store);
        var keeper = new CashKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyDeposit(alice, 1_000m);
        var seedSeq = dispatcher.Dispatch(
            new CashLedgerEvent
            {
                EndClientId = "alice",
                Operation = "Deposit",
                Amount = 1_000m,
                Currency = "BRL",
                Reference = "seed",
                OperatorId = "op",
            },
            () => { /* keeper already seeded above */ });

        // Wedge the next Append until the snapshot side has proven it
        // can (or cannot) acquire the lock.
        var appendStarted = new ManualResetEventSlim();
        var releaseAppend = new ManualResetEventSlim();
        store.OnAppend = () =>
        {
            appendStarted.Set();
            releaseAppend.Wait(TimeSpan.FromSeconds(5));
        };

        long snapBalance = -1;
        long snapSeq = -1;
        bool snapRanWhileAppendWedged = false;

        var snapshotThread = new Thread(() =>
        {
            // Wait until the dispatcher thread is mid-Append. Then try to
            // grab the snapshot lock — the fix MUST make this block.
            appendStarted.Wait(TimeSpan.FromSeconds(5));
            snapRanWhileAppendWedged = !releaseAppend.IsSet;
            dispatcher.WithSnapshotLock(seq =>
            {
                snapSeq = seq;
                snapBalance = (long)keeper.GetAvailable(alice);
            });
        });

        var withdrawThread = new Thread(() =>
        {
            // 5 seconds is plenty for the snapshot thread to attempt the
            // lock; releasing here lets Append complete.
            Thread.Sleep(50);
            releaseAppend.Set();
        });

        snapshotThread.Start();
        withdrawThread.Start();

        var outcome = dispatcher.DispatchWithPreApply(
            new CashLedgerEvent
            {
                EndClientId = "alice",
                Operation = "Withdrawal",
                Amount = 400m,
                Currency = "BRL",
                Reference = "w1",
                OperatorId = "op",
            },
            preApply: () => keeper.TryWithdraw(alice, 400m),
            rollback: () => keeper.ApplyDeposit(alice, 400m));

        Assert.True(snapshotThread.Join(TimeSpan.FromSeconds(10)));
        Assert.True(withdrawThread.Join(TimeSpan.FromSeconds(10)));

        Assert.True(outcome.Applied);
        Assert.True(outcome.Seq > seedSeq);

        // The snapshot lock was contested while Append was wedged.
        // After the fix the side thread must NOT be able to observe
        // state until the dispatcher releases the lock — which only
        // happens after Append returns. So the snapshot's balance is
        // either the pre-debit value (1000, if it grabbed the lock
        // before the dispatcher) or the post-append value (600, if
        // after); never the in-between 600-with-no-event-yet.
        Assert.True(snapRanWhileAppendWedged,
            "snapshot thread should have raced with the wedged Append");
        Assert.True(snapBalance == 1_000L || snapBalance == 600L,
            $"snapshot captured a torn balance ({snapBalance}); the fix's lock invariant is broken");

        // Strongest invariant: replay the WAL up to the captured
        // snapshot.seq and verify the keeper's snap-time balance equals
        // the projection. This is what recovery does on cold boot.
        var projected = ProjectBalance(store.Recorded, alice.Value, untilSeq: snapSeq);
        Assert.Equal(projected, snapBalance);
    }

    /// <summary>
    /// Two concurrent withdrawals that each ask for half the balance + 1
    /// can never both succeed. The fix routes both through the
    /// dispatcher lock, which serialises the two TryWithdraw checks so
    /// the first observes the full balance and the second observes the
    /// debited balance and is rejected.
    /// </summary>
    [Fact]
    public void Withdrawal_ConcurrentRace_AtMostOneSucceeds()
    {
        var dispatcher = new EventDispatcher(new NullEventStore());
        var keeper = new CashKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyDeposit(alice, 100m);

        var ready = new ManualResetEventSlim();
        DispatchOutcome o1 = default, o2 = default;

        var t1 = new Thread(() =>
        {
            ready.Wait();
            o1 = dispatcher.DispatchWithPreApply(
                NewWithdrawal(51m),
                preApply: () => keeper.TryWithdraw(alice, 51m),
                rollback: () => keeper.ApplyDeposit(alice, 51m));
        });
        var t2 = new Thread(() =>
        {
            ready.Wait();
            o2 = dispatcher.DispatchWithPreApply(
                NewWithdrawal(51m),
                preApply: () => keeper.TryWithdraw(alice, 51m),
                rollback: () => keeper.ApplyDeposit(alice, 51m));
        });

        t1.Start();
        t2.Start();
        ready.Set();
        Assert.True(t1.Join(TimeSpan.FromSeconds(5)));
        Assert.True(t2.Join(TimeSpan.FromSeconds(5)));

        // Exactly one succeeds.
        Assert.True(o1.Applied ^ o2.Applied);
        Assert.Equal(49m, keeper.GetAvailable(alice));

        static CashLedgerEvent NewWithdrawal(decimal amount) => new()
        {
            EndClientId = "alice",
            Operation = "Withdrawal",
            Amount = amount,
            Currency = "BRL",
            Reference = "race",
            OperatorId = "op",
        };
    }

    /// <summary>
    /// End-to-end equivalence: while a long-running flow alternates
    /// deposits and withdrawals, snapshots taken in parallel, when
    /// combined with the WAL tail, replay to the same balance as a
    /// straight WAL replay. This is the property snapshot recovery
    /// relies on (RFC §4.3 / §5.8) and is what the P1 fix protects.
    /// </summary>
    [Fact]
    public async Task Snapshot_DuringWithdrawals_ReplayMatchesDirectProjection()
    {
        long snapSeq;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var dispatcher = new EventDispatcher(store);
            var keeper = new CashKeeper();
            var alice = new EndClientId("alice");

            // Seed.
            DispatchDeposit(dispatcher, keeper, alice, 10_000m);

            // Drive withdrawals on a background thread; capture a
            // snapshot from the foreground a few times. The dispatcher
            // lock makes every snapshot's (seq, state) consistent.
            using var stop = new CancellationTokenSource();
            var worker = Task.Run(() =>
            {
                var rng = new Random(42);
                while (!stop.IsCancellationRequested)
                {
                    var amt = (decimal)rng.Next(1, 5);
                    try
                    {
                        dispatcher.DispatchWithPreApply(
                            new CashLedgerEvent
                            {
                                EndClientId = "alice",
                                Operation = "Withdrawal",
                                Amount = amt,
                                Currency = "BRL",
                                Reference = "load",
                                OperatorId = "op",
                            },
                            preApply: () => keeper.TryWithdraw(alice, amt),
                            rollback: () => keeper.ApplyDeposit(alice, amt));
                    }
                    catch (WalBackpressureException)
                    {
                        // The bounded WAL channel is intentionally
                        // smaller than this loop is willing to push;
                        // back off and retry so the test exercises
                        // sustained snapshot/append racing without
                        // failing on incidental backpressure. The
                        // dispatcher's rollback already restored the
                        // balance for the failed attempt.
                        Thread.Sleep(1);
                    }
                }
            });

            var (snapshotter, _) = BuildSnapshotterAndReplayer(keeper);
            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            for (var i = 0; i < 50; i++)
            {
                dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
                Thread.Sleep(2);
            }
            stop.Cancel();
            await worker;

            // Final snapshot to anchor the assertion.
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            snapSeq = snap!.Seq;

            await store.FlushAsync();
        }

        // Cold boot: snapshot + tail replay must match a from-WAL-only
        // replay of the same store.
        decimal snapPlusTail;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(keeper);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();
            snapPlusTail = keeper.GetAvailable(new EndClientId("alice"));
        }

        decimal walOnly;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var (_, replayer) = BuildSnapshotterAndReplayer(keeper);
            await foreach (var (_, evt) in store.ReadFromAsync(0))
                replayer.Apply(evt);
            walOnly = keeper.GetAvailable(new EndClientId("alice"));
        }

        Assert.Equal(walOnly, snapPlusTail);
        Assert.True(snapSeq > 0);
    }

    private static void DispatchDeposit(EventDispatcher d, CashKeeper k, EndClientId owner, decimal amount)
    {
        d.Dispatch(
            new CashLedgerEvent
            {
                EndClientId = owner.Value,
                Operation = "Deposit",
                Amount = amount,
                Currency = "BRL",
                Reference = "seed",
                OperatorId = "op",
            },
            () => k.ApplyDeposit(owner, amount));
    }

    private (StateSnapshotter, EventReplayer) BuildSnapshotterAndReplayer(CashKeeper keeper)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algos = new AlgoBook();
        var sink = new NullSink();
        var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var snapshotter = new StateSnapshotter(book, positions, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            clOrdIds, ownership, algos, new AlgoIdRegistry(),
            new CashLedger(),
            cashKeeper: keeper);
        var replayer = new EventReplayer(book, ownership, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            processor, algos, clOrdIds, new AlgoIdRegistry(),
            cashKeeper: keeper);
        return (snapshotter, replayer);
    }

    private static decimal ProjectBalance(IReadOnlyList<WalEvent> events, string endclient, long untilSeq)
    {
        // Seq is assigned in append order starting at 1.
        decimal balance = 0m;
        for (var i = 0; i < events.Count && (i + 1) <= untilSeq; i++)
        {
            if (events[i] is not CashLedgerEvent c) continue;
            if (c.EndClientId != endclient) continue;
            if (c.Operation == "Deposit") balance += c.Amount;
            else if (c.Operation == "Withdrawal") balance -= c.Amount;
        }
        return balance;
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }

    /// <summary>
    /// IEventStore that records every appended event and lets the test
    /// inject a callback to wedge the Append call. Used to deterministically
    /// expose a snapshot/append race window.
    /// </summary>
    private sealed class WedgeableStore : IEventStore
    {
        public List<WalEvent> Recorded { get; } = new();
        public Action? OnAppend;
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> _)
        {
            OnAppend?.Invoke();
            Recorded.Add(evt);
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            for (var i = 0; i < Recorded.Count; i++)
            {
                var seq = i + 1;
                if (seq > sinceSeqExclusive) yield return (seq, Recorded[i]);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
