using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

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

    [Fact]
    public async Task PublishedSnapshot_UsesAppliedPrefixAndWalGenerationEnvelope()
    {
        var options = NewOptions("versioned-envelope");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using var store = NewStore(options);
        var state = BuildState(store);
        var seq = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);

        Assert.True(await NewSnapshotService(
            state.Dispatcher,
            state.Snapshotter,
            snapshots,
            options).TryTakeSnapshotAsync());

        var snapshot = snapshots.LoadLatest();
        Assert.NotNull(snapshot);
        Assert.Equal(seq, snapshot.Seq);
        Assert.Equal(seq, state.Dispatcher.LastAppliedSeq);
        Assert.Equal(PlatformSnapshot.CurrentFormatVersion, snapshot.FormatVersion);
        Assert.Equal(store.WalGeneration, snapshot.WalGeneration);
        Assert.Equal(
            OutboundLedgerSnapshot.CurrentVersion,
            snapshot.OutboundLedger?.Version);
    }

    [Fact]
    public async Task ApplyFailure_LeavesAppliedPrefixBehindAndPublishesNoSnapshot()
    {
        var options = NewOptions("apply-failure");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using var store = NewStore(options);
        var state = BuildState(store);

        Assert.Throws<InvalidOperationException>(() =>
            state.Dispatcher.Dispatch(
                new KillSwitchToggledEvent
                {
                    Scope = "firm",
                    Target = "TEST",
                    Killed = true,
                },
                () => throw new InvalidOperationException("injected apply failure")));

        Assert.Equal(1, state.Dispatcher.CurrentSeq);
        Assert.Equal(0, state.Dispatcher.LastAppliedSeq);
        Assert.False(await NewSnapshotService(
            state.Dispatcher,
            state.Snapshotter,
            snapshots,
            options).TryTakeSnapshotAsync());
        Assert.Null(snapshots.LoadLatest());
    }

    [Fact]
    public async Task CaptureWaitsForInFlightApply_AndCannotOmitAppliedState()
    {
        var options = NewOptions("delayed-apply");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using var store = NewStore(options);
        var state = BuildState(store);
        var applyEntered = new ManualResetEventSlim();
        var releaseApply = new ManualResetEventSlim();
        var owner = new EndClientId("alice");

        var dispatch = Task.Run(() => state.Dispatcher.Dispatch(
            new OrderSubmittedEvent
            {
                ClOrdId = 1,
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
                applyEntered.Set();
                Assert.True(releaseApply.Wait(TimeSpan.FromSeconds(10)));
                state.Book.TryAdd(new Order(
                    1, owner, "PETR4", 4321, OrderSide.Buy, OrderType.Limit, 100, 30m));
                state.Ownership.Register(1, owner);
            }));
        Assert.True(applyEntered.Wait(TimeSpan.FromSeconds(5)));

        var service = NewSnapshotService(
            state.Dispatcher,
            state.Snapshotter,
            snapshots,
            options);
        var snapshotAttempt = Task.Run(() => service.TryTakeSnapshotAsync());
        await Task.Delay(50);
        Assert.False(snapshotAttempt.IsCompleted);

        releaseApply.Set();
        Assert.Equal(1, await dispatch);
        Assert.True(await snapshotAttempt);
        Assert.Single(snapshots.LoadLatest()!.WorkingOrders);
    }

    [Fact]
    public async Task Recovery_FallsBackFromWrongGenerationToOlderValidSnapshot()
    {
        var options = NewOptions("wrong-generation-fallback");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using (var store = NewStore(options))
        {
            var state = BuildState(store);
            var seq1 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
            await store.FlushThroughAsync(seq1);
            snapshots.Write(StateSnapshotter.Project(
                state.Snapshotter.CaptureRaw(seq1, store.WalGeneration)));

            var seq2 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 2);
            await store.FlushThroughAsync(seq2);
            state.Book.TryAdd(new Order(
                999,
                new EndClientId("ghost"),
                "GHOST",
                999,
                OrderSide.Buy,
                OrderType.Limit,
                1,
                1m));
            snapshots.Write(StateSnapshotter.Project(
                state.Snapshotter.CaptureRaw(seq2, Guid.NewGuid())));
        }

        await using var reopened = NewStore(options);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();

        Assert.True(recovered.Book.TryGet(1, out _));
        Assert.True(recovered.Book.TryGet(2, out _));
        Assert.False(recovered.Book.TryGet(999, out _));
    }

    [Fact]
    public async Task Recovery_FallsBackFromWrongLineageFutureSnapshot()
    {
        var options = NewOptions("wrong-lineage-future-fallback");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using (var store = NewStore(options))
        {
            var state = BuildState(store);
            var seq1 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
            await store.FlushThroughAsync(seq1);
            snapshots.Write(StateSnapshotter.Project(
                state.Snapshotter.CaptureRaw(seq1, store.WalGeneration)));

            var seq2 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 2);
            await store.FlushThroughAsync(seq2);
            snapshots.Write(new PlatformSnapshot
            {
                Seq = seq2,
                FormatVersion = PlatformSnapshot.CurrentFormatVersion + 1,
                WalGeneration = Guid.NewGuid(),
                OutboundLedger = new OutboundLedgerSnapshot
                {
                    Version = OutboundLedgerSnapshot.CurrentVersion,
                },
            });
        }

        await using var reopened = NewStore(options);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();

        Assert.True(recovered.Book.TryGet(1, out _));
        Assert.True(recovered.Book.TryGet(2, out _));
    }

    [Fact]
    public async Task Recovery_FallsBackFromSnapshotAheadOfMarkerToOlderValidSnapshot()
    {
        var options = NewOptions("ahead-fallback");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using (var store = NewStore(options))
        {
            var state = BuildState(store);
            var seq1 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
            await store.FlushThroughAsync(seq1);
            snapshots.Write(StateSnapshotter.Project(
                state.Snapshotter.CaptureRaw(seq1, store.WalGeneration)));

            var seq2 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 2);
            await store.FlushThroughAsync(seq2);
            state.Book.TryAdd(new Order(
                999,
                new EndClientId("ghost"),
                "GHOST",
                999,
                OrderSide.Buy,
                OrderType.Limit,
                1,
                1m));
            snapshots.Write(StateSnapshotter.Project(
                state.Snapshotter.CaptureRaw(seq2 + 1, store.WalGeneration)));
        }

        await using var reopened = NewStore(options);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();

        Assert.True(recovered.Book.TryGet(1, out _));
        Assert.True(recovered.Book.TryGet(2, out _));
        Assert.False(recovered.Book.TryGet(999, out _));
    }

    [Fact]
    public async Task Recovery_FallsBackFromCorruptNewestSnapshot()
    {
        var options = NewOptions("corrupt-newest-fallback");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using (var store = NewStore(options))
        {
            var state = BuildState(store);
            var seq1 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
            await store.FlushThroughAsync(seq1);
            snapshots.Write(StateSnapshotter.Project(
                state.Snapshotter.CaptureRaw(seq1, store.WalGeneration)));
            var seq2 = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 2);
            await store.FlushThroughAsync(seq2);
            snapshots.Write(StateSnapshotter.Project(
                state.Snapshotter.CaptureRaw(seq2, store.WalGeneration)));
            File.WriteAllText(
                Path.Combine(snapshots.Root, "snap-000000000002.json"),
                "{not-json");
        }

        await using var reopened = NewStore(options);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();

        Assert.True(recovered.Book.TryGet(1, out _));
        Assert.True(recovered.Book.TryGet(2, out _));
    }

    [Fact]
    public async Task LegacySnapshot_IsUsedOnlyWhenMarkerCoversItsSequence()
    {
        var coveredOptions = NewOptions("legacy-covered");
        var coveredSnapshots = new SnapshotStore(
            coveredOptions.DataDirectory,
            coveredOptions.FirmId);
        await using (var store = NewStore(coveredOptions))
        {
            var state = BuildState(store);
            var seq = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
            await store.FlushThroughAsync(seq);
            coveredSnapshots.Write(state.Snapshotter.Capture(seq));
        }

        await using (var reopened = NewStore(coveredOptions))
        {
            var tracking = new TrackingEventStore(reopened);
            var recovered = BuildState(tracking);
            await NewRecovery(tracking, recovered, coveredSnapshots).RunAsync();
            Assert.Equal(1, tracking.LastReadSince);
        }

        var uncoveredOptions = NewOptions("legacy-uncovered");
        var uncoveredSnapshots = new SnapshotStore(
            uncoveredOptions.DataDirectory,
            uncoveredOptions.FirmId);
        await using var emptyStore = NewStore(uncoveredOptions);
        var unsafeState = BuildState(emptyStore);
        uncoveredSnapshots.Write(unsafeState.Snapshotter.Capture(seq: 1));
        var emptyTracking = new TrackingEventStore(emptyStore);
        var emptyRecovered = BuildState(emptyTracking);
        await NewRecovery(emptyTracking, emptyRecovered, uncoveredSnapshots).RunAsync();
        Assert.Equal(0, emptyTracking.LastReadSince);
    }

    [Fact]
    public async Task UnknownFutureSnapshotOrLedgerVersion_FailsRecoveryClosed()
    {
        var options = NewOptions("future-version");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using var store = NewStore(options);
        var state = BuildState(store);
        var seq = DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
        await store.FlushThroughAsync(seq);
        snapshots.Write(new PlatformSnapshot
        {
            Seq = seq,
            FormatVersion = PlatformSnapshot.CurrentFormatVersion + 1,
            WalGeneration = store.WalGeneration,
            OutboundLedger = new OutboundLedgerSnapshot
            {
                Version = OutboundLedgerSnapshot.CurrentVersion,
            },
        });

        Assert.Throws<SnapshotRecoveryException>(() =>
            NewRecovery(store, BuildState(store), snapshots)
                .RunAsync()
                .GetAwaiter()
                .GetResult());

        snapshots.Write(new PlatformSnapshot
        {
            Seq = seq,
            FormatVersion = PlatformSnapshot.CurrentFormatVersion,
            WalGeneration = store.WalGeneration,
            OutboundLedger = new OutboundLedgerSnapshot
            {
                Version = OutboundLedgerSnapshot.CurrentVersion + 1,
            },
        });
        Assert.Throws<SnapshotRecoveryException>(() =>
            NewRecovery(store, BuildState(store), snapshots)
                .RunAsync()
                .GetAwaiter()
                .GetResult());
    }

    [Fact]
    public void SnapshotStore_CleansRecognizedStaging_AndRejectsUnknownOrSymlinkArtifacts()
    {
        var options = NewOptions("snapshot-artifacts");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        var staging = Path.Combine(
            snapshots.Root,
            "snap-000000000001.json.writing");
        File.WriteAllText(staging, "{}");
        Assert.Null(snapshots.LoadLatest());
        Assert.False(File.Exists(staging));

        var unknown = Path.Combine(snapshots.Root, "unexpected.bin");
        File.WriteAllText(unknown, "x");
        Assert.Throws<SnapshotRecoveryException>(() => snapshots.LoadLatest());
        File.Delete(unknown);

        if (OperatingSystem.IsLinux())
        {
            var target = Path.Combine(
                options.DataDirectory,
                options.FirmId,
                "snapshot-target.json");
            File.WriteAllText(target, "{}");
            var link = Path.Combine(snapshots.Root, "snap-000000000001.json");
            File.CreateSymbolicLink(link, target);
            Assert.Throws<SnapshotRecoveryException>(() => snapshots.LoadLatest());

            File.Delete(link);
            File.Delete(target);
            var danglingTarget = Path.Combine(
                options.DataDirectory,
                options.FirmId,
                "dangling-target.json");
            var stagingLink = Path.Combine(
                snapshots.Root,
                "snap-000000000001.json.writing");
            File.CreateSymbolicLink(stagingLink, danglingTarget);
            Assert.Throws<SnapshotRecoveryException>(() =>
                snapshots.Write(new PlatformSnapshot { Seq = 1 }));
            Assert.False(File.Exists(danglingTarget));
        }
    }

    [Fact]
    public async Task SnapshotGeneration_SurvivesSegmentRotationAndRestart()
    {
        var options = NewOptions("rotation-restart");
        options.SegmentMaxBytes = 256;
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        Guid generation;
        await using (var store = NewStore(options))
        {
            generation = store.WalGeneration;
            var state = BuildState(store);
            for (ulong id = 1; id <= 12; id++)
                DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, id);
            Assert.True(await NewSnapshotService(
                state.Dispatcher,
                state.Snapshotter,
                snapshots,
                options).TryTakeSnapshotAsync());
        }

        await using var reopened = NewStore(options);
        Assert.Equal(generation, reopened.WalGeneration);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();
        Assert.Equal(12, recovered.Book.Snapshot().Count());
    }

    [Fact]
    public async Task VersionedSnapshotPlusTail_DoesNotDoubleApplyOrOmitFills()
    {
        var options = NewOptions("snapshot-tail-fills");
        var snapshots = new SnapshotStore(options.DataDirectory, options.FirmId);
        await using (var store = NewStore(options))
        {
            var state = BuildState(store);
            DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, 1);
            for (var cumulative = 1; cumulative <= 50; cumulative++)
                DispatchFill(state, cumulative);

            Assert.True(await NewSnapshotService(
                state.Dispatcher,
                state.Snapshotter,
                snapshots,
                options).TryTakeSnapshotAsync());

            for (var cumulative = 51; cumulative <= 100; cumulative++)
                DispatchFill(state, cumulative);
            await store.FlushAsync();
        }

        await using var reopened = NewStore(options);
        var recovered = BuildState(reopened);
        await NewRecovery(reopened, recovered, snapshots).RunAsync();

        var position = Assert.Single(
            recovered.Positions.ForEndClient(new EndClientId("alice")));
        Assert.Equal(100, position.NetQuantity);
        Assert.True(recovered.Book.TryGet(1, out var order));
        Assert.Equal(100, order!.CumulativeQuantity);
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
            positions,
            killSwitch,
            ownership,
            algos,
            processor,
            snapshotter,
            new EventDispatcher(store));
    }

    private static void DispatchFill(TestState state, long cumulative)
    {
        var evt = new ExecutionReportReceivedEvent
        {
            ClOrdId = 1,
            ExecKind = (cumulative == 100
                ? ExecKind.Fill
                : ExecKind.PartialFill).ToString(),
            LeavesQuantity = 100 - cumulative,
            CumulativeQuantity = cumulative,
            LastQuantity = 1,
            LastPrice = 30m,
            Synthetic = false,
        };
        state.Dispatcher.Dispatch(
            evt,
            () => state.Processor.Apply(
                1,
                cumulative == 100 ? ExecKind.Fill : ExecKind.PartialFill,
                100 - cumulative,
                cumulative,
                1,
                30m,
                null));
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

    private sealed class TrackingEventStore(IEventStore inner) : IEventStore
    {
        public long? LastReadSince { get; private set; }
        public long CurrentSeq => inner.CurrentSeq;
        public Guid WalGeneration => inner.WalGeneration;
        public long LastAdmittedSeq => inner.LastAdmittedSeq;
        public long LastAppendedSeq => inner.LastAppendedSeq;
        public long LastLogFsyncedSeq => inner.LastLogFsyncedSeq;
        public long LastCommittedSeq => inner.LastCommittedSeq;
        public long Append(WalEvent evt) => inner.Append(evt);
        public long Append(WalEvent evt, ReadOnlyMemory<byte> payload) =>
            inner.Append(evt, payload);
        public ValueTask FlushAsync(CancellationToken ct = default) =>
            inner.FlushAsync(ct);
        public ValueTask FlushThroughAsync(long seq, CancellationToken ct = default) =>
            inner.FlushThroughAsync(seq, ct);

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastReadSince = sinceSeqExclusive;
            await foreach (var item in inner.ReadFromAsync(sinceSeqExclusive, ct))
                yield return item;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record TestState(
        WorkingOrderBook Book,
        PositionKeeper Positions,
        KillSwitchService KillSwitch,
        OrderOwnershipMap Ownership,
        AlgoBook Algos,
        ExecutionReportProcessor Processor,
        StateSnapshotter Snapshotter,
        EventDispatcher Dispatcher);
}
