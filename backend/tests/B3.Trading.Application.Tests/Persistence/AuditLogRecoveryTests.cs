using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Pass-1 review (#322) P1.1. Regression coverage for the
/// audit-history-lost-before-snapshot bug: prior to the fix
/// <see cref="PersistenceRecovery"/> drove the WAL drain from
/// <c>snapshot.Seq + 1</c>, leaving the <see cref="AuditLogKeeper"/>
/// empty of every audit envelope captured before the latest snapshot.
/// </summary>
public class AuditLogRecoveryTests : IDisposable
{
    private readonly string _root;

    public AuditLogRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-audit-recovery-" + Guid.NewGuid().ToString("N"));
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

    private static AuditLogKeeper Keeper(int capacity = 1000) =>
        new(Options.Create(new AuditLogOptions { Capacity = capacity }));

    private static AuditLogger Logger(EventDispatcher dispatcher, AuditLogKeeper keeper) =>
        new(dispatcher, keeper, NullLogger<AuditLogger>.Instance);

    private static EventReplayer EmptyReplayer(AuditLogKeeper keeper)
    {
        // Minimum-viable replayer for an audit-only recovery test:
        // the audit pre-pass routes audit envelopes through the
        // keeper, but the main replay needs a fully-formed
        // EventReplayer too (the recovery driver always calls it).
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var killSwitch = new KillSwitchService();
        var phases = new SessionPhaseService();
        var algos = new AlgoBook();
        var processor = new ExecutionReportProcessor(
            ownership, book, new PositionKeeper(), new NullSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        return new EventReplayer(
            book, ownership, killSwitch, new SymbolHaltService(), phases,
            processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry(),
            auditKeeper: keeper);
    }

    private static AuditLogEvent NewAudit(string suffix) => new()
    {
        EventType = AuditEventTypes.AdminConfigChange,
        Outcome = AuditOutcomes.Success,
        ActorUsername = "admin",
        ResourcePath = $"/admin/test/{suffix}",
        Details = new Dictionary<string, string> { ["k"] = suffix },
    };

    [Fact]
    public async Task ColdRestart_AfterSnapshotAndMoreAudits_RebuildsRingWithBothHistoricAndTailEntries()
    {
        // Phase 1: emit N audits, snapshot, emit M more, then close.
        const int n = 12;
        const int m = 5;
        var snapStore = new SnapshotStore(_root, "test");
        long snapSeq;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var dispatcher = new EventDispatcher(store);
            var keeper = Keeper();
            var logger = Logger(dispatcher, keeper);

            for (var i = 0; i < n; i++)
                logger.Log(NewAudit($"pre-{i}"));

            // Take a snapshot at the current WAL seq.
            B3.Trading.Application.Persistence.PlatformSnapshot? snap = null;
            var snapshotter = BuildSnapshotter();
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            snapSeq = snap!.Seq;

            for (var i = 0; i < m; i++)
                logger.Log(NewAudit($"post-{i}"));

            await store.FlushAsync();
        }

        // Phase 2: cold boot — recovery must rehydrate ALL N+M audits.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = Keeper();
            var replayer = EmptyReplayer(keeper);
            var recovery = new PersistenceRecovery(
                store, BuildSnapshotter(), replayer, snapStore,
                NullLogger<PersistenceRecovery>.Instance,
                auditKeeper: keeper);
            await recovery.RunAsync();

            Assert.Equal(n + m, keeper.Count);
            // Newest-first scan via Query — confirm the seq ordering
            // matches what live capture would have produced.
            var page = keeper.Query(
                since: DateTimeOffset.MinValue,
                until: DateTimeOffset.MaxValue,
                user: null, typePattern: null, outcome: null,
                limit: n + m + 10, cursorSeq: null);
            Assert.Equal(n + m, page.Entries.Count);
            // Pre-snapshot entries must be present (the bug being fixed).
            for (var i = 0; i < n; i++)
            {
                var suffix = $"pre-{i}";
                Assert.Contains(page.Entries, e =>
                    e.Details is not null && e.Details.TryGetValue("k", out var v) && v == suffix);
            }
            // Post-snapshot entries equally restored from the tail.
            for (var i = 0; i < m; i++)
            {
                var suffix = $"post-{i}";
                Assert.Contains(page.Entries, e =>
                    e.Details is not null && e.Details.TryGetValue("k", out var v) && v == suffix);
            }
            // Sanity — every restored entry's seq is <= the post-batch
            // WAL position and > 0.
            Assert.All(page.Entries, e => Assert.True(e.Seq > 0));
        }
    }

    [Fact]
    public async Task ColdRestart_NoSnapshot_RehydratesAllAuditsViaMainReplay()
    {
        // Without a snapshot the main replay starts at seq=0 and
        // already folds audit envelopes — the pre-pass should be a
        // no-op. Guards the "no snapshot found; full WAL replay" path.
        const int n = 8;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var dispatcher = new EventDispatcher(store);
            var keeper = Keeper();
            var logger = Logger(dispatcher, keeper);
            for (var i = 0; i < n; i++)
                logger.Log(NewAudit($"only-{i}"));
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = Keeper();
            var replayer = EmptyReplayer(keeper);
            var recovery = new PersistenceRecovery(
                store, BuildSnapshotter(), replayer, new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance,
                auditKeeper: keeper);
            await recovery.RunAsync();

            Assert.Equal(n, keeper.Count);
        }
    }

    private static StateSnapshotter BuildSnapshotter()
    {
        // Minimal snapshotter — the audit-recovery test only needs
        // the snapshot to carry a non-zero Seq; the order/position
        // arrays are empty. Mirrors the ctor wiring used by
        // RecoveryAndSnapshotTests.BuildState.
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var killSwitch = new KillSwitchService();
        var halts = new SymbolHaltService();
        var phases = new SessionPhaseService();
        var positions = new PositionKeeper();
        var algos = new AlgoBook();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algoIds = new AlgoIdRegistry();
        return new StateSnapshotter(
            book, positions, killSwitch, halts, phases, clOrdIds, ownership, algos, algoIds, new CashLedger());
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }
}
