using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Persistence;

public sealed class SnapshotCommittedPrefixTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "snapshot-prefix-tests",
        Guid.NewGuid().ToString("N"));

    public SnapshotCommittedPrefixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task FenceFault_PublishesNoSnapshot_AndRestartDoesNotResurrectTail()
    {
        var options = NewOptions("fence-fault");
        var hooks = new BlockingSnapshotHook(throwOnRelease: true);
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);

        await using (var store = NewStore(options, hooks))
        {
            var state = BuildState(store);
            DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
            Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));

            var service = NewSnapshotService(
                state.Dispatcher, state.Snapshotter, snapshots, options);
            var attempt = service.TryTakeSnapshotAsync();
            hooks.Release.Set();

            Assert.False(await attempt);
            Assert.Null(snapshots.LoadLatest());
            Assert.Empty(Directory.EnumerateFiles(
                snapshots.Root, "snap-*.json", SearchOption.TopDirectoryOnly));
        }

        await using var reopened = NewStore(options);
        Assert.Equal(0, reopened.LastCommittedSeq);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();
        Assert.False(recovered.Book.TryGet(1, out _));
    }

    [Fact]
    public async Task CancelledFence_PublishesNothing_EvenWhenWalLaterCommits()
    {
        var options = NewOptions("fence-cancel");
        var hooks = new BlockingSnapshotHook(throwOnRelease: false);
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using var store = NewStore(options, hooks);
        var state = BuildState(store);
        var seq = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
        Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));

        var service = NewSnapshotService(
            state.Dispatcher, state.Snapshotter, snapshots, options);
        using var cancellation = new CancellationTokenSource();
        var attempt = service.TryTakeSnapshotAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(await attempt);
        Assert.Null(snapshots.LoadLatest());

        hooks.Release.Set();
        await store.FlushThroughAsync(seq);
        Assert.Equal(seq, store.LastCommittedSeq);
        Assert.Null(snapshots.LoadLatest());
    }

    [Fact]
    public async Task Recovery_IgnoresOnDiskSnapshotAheadOfCommittedMarker()
    {
        var options = NewOptions("ahead-recovery");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using (var store = NewStore(options))
        {
            var unsafeState = BuildState(store);
            var owner = new EndClientId("alice");
            unsafeState.Book.TryAdd(new Order(
                1,
                owner,
                "PETR4",
                4321,
                OrderSide.Buy,
                OrderType.Limit,
                100,
                30m));
            unsafeState.Ownership.Register(1, owner);
            snapshots.Write(unsafeState.Snapshotter.Capture(seq: 1));
            Assert.Equal(0, store.LastCommittedSeq);
        }

        await using var reopened = NewStore(options);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();

        Assert.Equal(0, reopened.LastCommittedSeq);
        Assert.False(recovered.Book.TryGet(1, out _));
        Assert.NotNull(snapshots.LoadLatest());
    }

    private PersistenceOptions NewOptions(string name) => new()
    {
        DataDirectory = Path.Combine(_root, name),
        FirmId = "test",
        Enabled = true,
        ChannelCapacity = 64,
        GroupCommitMaxRecords = 16,
        GroupCommitWindow = TimeSpan.FromMilliseconds(5),
        FsyncOnFlush = false,
    };

    private static FileEventStore NewStore(
        PersistenceOptions options,
        IWalCommitBoundaryHooks? hooks = null) =>
        new(
            options,
            NullLogger<FileEventStore>.Instance,
            ReconciliationDirectoryDurability.Instance,
            hooks ?? NoOpWalCommitBoundaryHooks.Instance);

    private static SnapshotService NewSnapshotService(
        EventDispatcher dispatcher,
        StateSnapshotter snapshotter,
        SnapshotStore store,
        PersistenceOptions options) =>
        new(
            dispatcher,
            snapshotter,
            store,
            Options.Create(options),
            NullLogger<SnapshotService>.Instance);

    private static PersistenceRecovery NewRecovery(
        IEventStore store,
        TestState state,
        SnapshotStore snapshots)
    {
        var replayer = new EventReplayer(
            state.Book,
            state.Ownership,
            state.KillSwitch,
            new SymbolHaltService(),
            new SessionPhaseService(),
            state.Processor,
            state.Algos,
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry());
        return new PersistenceRecovery(
            store,
            state.Snapshotter,
            replayer,
            snapshots,
            NullLogger<PersistenceRecovery>.Instance);
    }

    private static TestState BuildState(IEventStore store)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var algos = new AlgoBook();
        var processor = new ExecutionReportProcessor(
            ownership,
            book,
            positions,
            new NullExecutionSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var snapshotter = new StateSnapshotter(
            book,
            positions,
            killSwitch,
            new SymbolHaltService(),
            new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(),
            ownership,
            algos,
            new AlgoIdRegistry(),
            new CashLedger());
        return new TestState(
            book,
            killSwitch,
            ownership,
            algos,
            processor,
            snapshotter,
            new EventDispatcher(store));
    }

    private static long DispatchSubmit(
        EventDispatcher dispatcher,
        WorkingOrderBook book,
        OrderOwnershipMap ownership,
        ulong clOrdId)
    {
        var owner = new EndClientId("alice");
        return dispatcher.Dispatch(
            new OrderSubmittedEvent
            {
                ClOrdId = clOrdId,
                EndClientId = "alice",
                FirmId = "TEST",
                Symbol = "PETR4",
                SecurityId = 4321,
                Side = "Buy",
                Type = "Limit",
                Quantity = 100,
                Price = 30m,
            },
            () =>
            {
                book.TryAdd(new Order(
                    clOrdId,
                    owner,
                    "PETR4",
                    4321,
                    OrderSide.Buy,
                    OrderType.Limit,
                    100,
                    30m));
                ownership.Register(clOrdId, owner);
            });
    }

    private sealed class BlockingSnapshotHook(bool throwOnRelease)
        : IWalCommitBoundaryHooks
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public void OnBoundary(WalCommitBoundary boundary, long seq)
        {
            if (boundary != WalCommitBoundary.BeforeMarkerStage)
                return;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release snapshot fence.");
            if (throwOnRelease)
                throw new IOException(
                    $"Injected pre-marker snapshot fence fault for seq {seq}.");
        }
    }

    private sealed class NullExecutionSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }

    private sealed record TestState(
        WorkingOrderBook Book,
        KillSwitchService KillSwitch,
        OrderOwnershipMap Ownership,
        AlgoBook Algos,
        ExecutionReportProcessor Processor,
        StateSnapshotter Snapshotter,
        EventDispatcher Dispatcher);
}
