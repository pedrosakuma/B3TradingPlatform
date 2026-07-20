using System.Text.Json;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public sealed class LifecycleAtomicityTests
{
    private static readonly EndClientId Owner = new("alice");

    [Fact]
    public async Task ConcurrentModify_Barrier_AllowsExactlyOneGatewayCall()
    {
        var gateway = new TestGateway { BlockReplace = true };
        var harness = BuildModify(gateway);
        using var barrier = new Barrier(3);
        var request = new OrderModifyRequest(Owner, 100, 120, 31m);

        var first = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.Service.ModifyAsync(request, CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.Service.ModifyAsync(request, CancellationToken.None);
        });

        barrier.SignalAndWait();
        await gateway.ReplaceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ReleaseReplace.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, gateway.ReplaceCalls);
        Assert.Single(results, static r => r.Kind == OrderModifyResultKind.Accepted);
        Assert.Single(results, static r => r.Kind == OrderModifyResultKind.Conflict);
    }

    [Fact]
    public async Task PreSendReplaceFailure_AppendsTerminalResolution_AndReplayDoesNotResurrect()
    {
        var store = new RecordingEventStore();
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var harness = BuildModify(gateway, store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.GatewayFailed, result.Kind);
        Assert.Collection(store.Events,
            static e => Assert.IsType<OrderReplaceRequestedEvent>(e),
            static e => Assert.IsType<OrderReplacePreSendFailedEvent>(e));
        Assert.False(harness.Replacements.IsOriginalInFlight(100));

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);

        Assert.False(recovered.Replacements.IsOriginalInFlight(100));
    }

    [Fact]
    public async Task PreSendOnlyGateway_PreservesLegacyExceptionContract_AndTerminalisesReplace()
    {
        var store = new RecordingEventStore();
        var harness = BuildModify(new UnavailableExchangeGateway(), store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.GatewayFailed, result.Kind);
        Assert.IsType<InvalidOperationException>(result.GatewayException);
        Assert.False(harness.Replacements.IsOriginalInFlight(100));
        Assert.IsType<OrderReplacePreSendFailedEvent>(store.Events[^1]);
    }

    [Fact]
    public async Task AmbiguousReplaceFailure_RemainsPendingAcrossReplay()
    {
        var store = new RecordingEventStore();
        var gateway = new TestGateway
        {
            ReplaceException = new IOException("write outcome unknown"),
        };
        var harness = BuildModify(gateway, store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.GatewayAmbiguous, result.Kind);
        Assert.True(harness.Replacements.IsOriginalInFlight(100));
        Assert.IsType<OrderReplaceAmbiguousMarginHeldEvent>(store.Events[^1]);

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);

        Assert.True(recovered.Replacements.IsOriginalInFlight(100));
        Assert.True(Assert.Single(recovered.Replacements.Snapshot()).AmbiguousMarginHeld);
    }

    [Fact]
    public async Task PreSendResolutionWalBackpressure_DrainsAndDoesNotLeaveLivePendingIntent()
    {
        var store = new RecordingEventStore(
            failOnAppend: 2,
            failureFactory: static () => new WalBackpressureException("resolution lane full"));
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var harness = BuildModify(gateway, store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.ReconciliationRequired, result.Kind);
        Assert.Equal("pre_send_resolution_not_durable", result.Reason);
        Assert.True(harness.Drain.IsDraining);
        Assert.False(harness.Replacements.IsOriginalInFlight(100));
        Assert.Single(harness.Margin.Aborted);
        Assert.Single(store.Events);
        Assert.IsType<OrderReplaceRequestedEvent>(store.Events[0]);
    }

    [Fact]
    public async Task PreSendResolutionTransientBackpressure_RetriesAndFlushesBeforeReturning()
    {
        var store = new RecordingEventStore(
            failOnAppend: 2,
            failureFactory: static () => new WalBackpressureException("transient"),
            failureCount: 1);
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var harness = BuildModify(gateway, store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.GatewayFailed, result.Kind);
        Assert.False(harness.Drain.IsDraining);
        Assert.Empty(harness.Markers.Load());
        Assert.Equal(2, store.Events.Count);
        Assert.True(store.FlushCalls > 0);
    }

    [Fact]
    public async Task ReplacePreSend_WhenMarkerAndWalBothFail_RetainsUnresolvedIntentAcrossRestart()
    {
        var markers = new FaultingMarkerStore(durablyPublished: false);
        var store = PermanentResolutionFaultStore();
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var runtime = BuildModify(gateway, store, markers);

        var result = await runtime.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.ReconciliationRequired, result.Kind);
        Assert.True(runtime.Drain.IsDraining);
        Assert.True(runtime.Replacements.IsOriginalInFlight(100));
        Assert.Empty(runtime.Margin.Aborted);
        Assert.Empty(markers.Load());

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        Assert.True(recovered.Replacements.IsOriginalInFlight(100));
        var startupDrain = new NeverDrainController();
        Assert.Equal(1, CreateColdStartGuard(
            recovered.PendingCancels, recovered.Replacements,
            startupDrain).Apply());
        Assert.True(startupDrain.IsDraining);
        Assert.True(recovered.Replacements.IsOriginalInFlight(100));
    }

    [Fact]
    public async Task ReplaceAmbiguous_WhenMarkerAndWalBothFail_RetainsPlainUnresolvedIntent()
    {
        var markers = new FaultingMarkerStore(durablyPublished: false);
        var store = PermanentResolutionFaultStore();
        var gateway = new TestGateway
        {
            ReplaceException = new IOException("wire outcome unknown"),
        };
        var runtime = BuildModify(gateway, store, markers);

        var result = await runtime.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.ReconciliationRequired, result.Kind);
        Assert.True(runtime.Drain.IsDraining);
        Assert.True(runtime.Replacements.IsOriginalInFlight(100));
        Assert.False(Assert.Single(
            runtime.Replacements.Snapshot()).AmbiguousMarginHeld);
        Assert.Empty(runtime.Margin.Aborted);
        Assert.Empty(markers.Load());

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        var startupDrain = new NeverDrainController();
        Assert.Equal(1, CreateColdStartGuard(
            recovered.PendingCancels, recovered.Replacements,
            startupDrain).Apply());
        Assert.True(startupDrain.IsDraining);
        Assert.False(Assert.Single(
            recovered.Replacements.Snapshot()).AmbiguousMarginHeld);
    }

    [Fact]
    public async Task ReplacePreSend_WhenMarkerFailsButWalSucceeds_UsesWalResolution()
    {
        var markers = new FaultingMarkerStore(durablyPublished: false);
        var store = new RecordingEventStore();
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var runtime = BuildModify(gateway, store, markers);

        var result = await runtime.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.GatewayFailed, result.Kind);
        Assert.False(runtime.Drain.IsDraining);
        Assert.False(runtime.Replacements.IsOriginalInFlight(100));
        Assert.Collection(store.Events,
            static e => Assert.IsType<OrderReplaceRequestedEvent>(e),
            static e => Assert.IsType<OrderReplacePreSendFailedEvent>(e));
    }

    [Fact]
    public async Task AmbiguousResolutionWalFault_DrainsAndMarksIntentInMemoryForTtl()
    {
        var store = new RecordingEventStore(
            failOnAppend: 2,
            failureFactory: static () => new WalFaultedException(
                "resolution writer faulted", new IOException("disk full")));
        var gateway = new TestGateway
        {
            ReplaceException = new IOException("wire outcome unknown"),
        };
        var harness = BuildModify(gateway, store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.ReconciliationRequired, result.Kind);
        Assert.Equal("ambiguous_resolution_not_durable", result.Reason);
        Assert.True(harness.Drain.IsDraining);
        var pending = Assert.Single(harness.Replacements.Snapshot());
        Assert.True(pending.AmbiguousMarginHeld);
        Assert.Empty(harness.Margin.Aborted);
        Assert.Single(store.Events);
        Assert.IsType<OrderReplaceRequestedEvent>(store.Events[0]);
    }

    [Fact]
    public async Task CrashBeforeSnapshot_CancelPreSendMarkerCleansReplayAndKeepsStartupDrained()
    {
        var markers = new InMemoryReconciliationMarkerStore();
        var store = new RecordingEventStore(
            failOnAppend: 2,
            failureFactory: static () => new WalFaultedException(
                "resolution fault", new IOException("disk full")));
        var dispatcher = new EventDispatcher(store);
        var pending = new PendingCancelRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var drain = new NeverDrainController();
        var service = new OrderCancelService(
            new ClOrdIdPrefixRegistry(), ownership, book,
            new UnavailableExchangeGateway(), dispatcher,
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: pending,
            reconciliationDrain: drain,
            resolutionWriter: new ReconciliationResolutionWriter(
                markers, dispatcher,
                NullLogger<ReconciliationResolutionWriter>.Instance));

        var result = await service.CancelAsync(Owner, 100, CancellationToken.None);
        Assert.Equal(OrderCancelResultKind.ReconciliationRequired, result.Kind);
        Assert.Single(markers.Load());
        Assert.Single(store.Events);

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        Assert.True(recovered.PendingCancels.TryGetByCancel(
            result.CancelClOrdId, out _));
        var startupDrain = new NeverDrainController();
        var startup = CreateMarkerRecovery(
            markers, recovered.Dispatcher, recovered.PendingCancels,
            recovered.Replacements, recovered.Ownership, recovered.ClOrdIds,
            startupDrain);

        Assert.Equal(1, startup.Apply());
        Assert.True(startupDrain.IsDraining);
        Assert.Equal(0, recovered.PendingCancels.CountForTesting);
        Assert.False(recovered.Ownership.TryResolveOrig(
            result.CancelClOrdId, out _));
        Assert.Single(markers.Load());

        var retryGateway = new TestGateway();
        var blockedRetry = new OrderCancelService(
            recovered.ClOrdIds, recovered.Ownership, recovered.Book, retryGateway,
            recovered.Dispatcher, NullLogger<OrderCancelService>.Instance,
            pendingCancels: recovered.PendingCancels,
            reconciliationDrain: startupDrain);
        Assert.Equal(
            OrderCancelResultKind.ReconciliationRequired,
            (await blockedRetry.CancelAsync(Owner, 100, CancellationToken.None)).Kind);
        Assert.Equal(0, retryGateway.CancelCalls);
    }

    [Fact]
    public async Task CrashBeforeSnapshot_ReplacePreSendMarkerConsumesReplayAndDrainsStartup()
    {
        var markers = new InMemoryReconciliationMarkerStore();
        var store = PermanentResolutionFaultStore();
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var runtime = BuildModify(gateway, store, markers);
        var result = await runtime.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);
        Assert.Equal(OrderModifyResultKind.ReconciliationRequired, result.Kind);
        Assert.Single(markers.Load());

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        Assert.True(recovered.Replacements.IsOriginalInFlight(100));
        var startupDrain = new NeverDrainController();

        Assert.Equal(1, CreateMarkerRecovery(
            markers, recovered.Dispatcher, recovered.PendingCancels,
            recovered.Replacements, recovered.Ownership, recovered.ClOrdIds,
            startupDrain).Apply());
        Assert.True(startupDrain.IsDraining);
        Assert.False(recovered.Replacements.IsOriginalInFlight(100));
        Assert.False(recovered.Ownership.TryResolveOrig(
            result.NewClOrdId, out _));
        Assert.Single(markers.Load());
    }

    [Fact]
    public async Task CrashBeforeSnapshot_ReplaceAmbiguousMarkerRestoresTtlStateAndDrainsStartup()
    {
        var markers = new InMemoryReconciliationMarkerStore();
        var store = PermanentResolutionFaultStore();
        var gateway = new TestGateway
        {
            ReplaceException = new IOException("wire outcome unknown"),
        };
        var runtime = BuildModify(gateway, store, markers);
        var result = await runtime.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);
        Assert.Equal(OrderModifyResultKind.ReconciliationRequired, result.Kind);

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        Assert.False(Assert.Single(
            recovered.Replacements.Snapshot()).AmbiguousMarginHeld);
        var startupDrain = new NeverDrainController();

        Assert.Equal(1, CreateMarkerRecovery(
            markers, recovered.Dispatcher, recovered.PendingCancels,
            recovered.Replacements, recovered.Ownership, recovered.ClOrdIds,
            startupDrain).Apply());
        Assert.True(startupDrain.IsDraining);
        Assert.True(Assert.Single(
            recovered.Replacements.Snapshot()).AmbiguousMarginHeld);
        Assert.Single(markers.Load());
    }

    [Fact]
    public void FileReconciliationMarkerStore_SurvivesStoreRecreation()
    {
        var root = Path.Combine(
            Environment.CurrentDirectory,
            "TestResults",
            "reconciliation-markers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new PersistenceOptions
            {
                DataDirectory = root,
                FirmId = "FIRM",
            };
            var marker = new ReconciliationMarker(
                ReconciliationMarkerKind.CancelPreSend,
                OriginalClOrdId: 100,
                MutationClOrdId: 200,
                OwnerEndClientId: Owner.Value);
            new FileReconciliationMarkerStore(options).Persist(marker);

            var recovered = Assert.Single(
                new FileReconciliationMarkerStore(options).Load());
            Assert.Equal(marker, recovered);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_FlushesDirectoryAfterRenameAndDelete()
    {
        var root = MarkerTestRoot();
        try
        {
            var options = MarkerOptions(root);
            Directory.CreateDirectory(
                Path.Combine(root, "FIRM", "reconciliation"));
            var durability = new RecordingDirectoryDurability();
            var store = new FileReconciliationMarkerStore(options, durability);
            var marker = CancelMarker();
            durability.OnFlush = directory =>
            {
                var markerPath = Path.Combine(directory, marker.Id + ".json");
                if (durability.Calls == 1)
                {
                    Assert.False(File.Exists(markerPath));
                    Assert.True(File.Exists(markerPath + ".writing"));
                }
                else if (durability.Calls == 2)
                {
                    Assert.True(File.Exists(markerPath));
                    Assert.False(File.Exists(markerPath + ".writing"));
                }
                else
                {
                    Assert.False(File.Exists(markerPath));
                    Assert.False(File.Exists(markerPath + ".writing"));
                }
            };

            store.Persist(marker);
            store.Remove(marker.Id);

            Assert.Equal(3, durability.Calls);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_FirstCreationFsyncsEachParentInOrder()
    {
        var root = MarkerTestRoot();
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            var firm = Path.Combine(data, "FIRM");
            var reconciliation = Path.Combine(firm, "reconciliation");
            var durability = new RecordingDirectoryDurability();
            durability.OnFlush = parent =>
            {
                var created = durability.Calls switch
                {
                    1 => data,
                    2 => firm,
                    3 => reconciliation,
                    _ => throw new InvalidOperationException("unexpected fsync"),
                };
                Assert.True(Directory.Exists(created));
            };

            _ = new FileReconciliationMarkerStore(
                new PersistenceOptions
                {
                    DataDirectory = data,
                    FirmId = "FIRM",
                },
                durability);

            Assert.Equal(
                new[] { root, data, firm },
                durability.Paths);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_ExistingDirectoryDoesNotRequireCreationFsync()
    {
        var root = MarkerTestRoot();
        var options = MarkerOptions(root);
        Directory.CreateDirectory(Path.Combine(root, "FIRM", "reconciliation"));
        try
        {
            var durability = new RecordingDirectoryDurability();

            _ = new FileReconciliationMarkerStore(options, durability);

            Assert.Equal(0, durability.Calls);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_FirstCreationPropagatesParentFsyncFailure()
    {
        var root = MarkerTestRoot();
        Directory.CreateDirectory(root);
        try
        {
            var durability = new RecordingDirectoryDurability
            {
                Failure = new IOException("parent directory fsync failed"),
            };

            Assert.Throws<IOException>(() =>
                new FileReconciliationMarkerStore(
                    new PersistenceOptions
                    {
                        DataDirectory = Path.Combine(root, "data"),
                        FirmId = "FIRM",
                    },
                    durability));
            Assert.Equal(1, durability.Calls);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_PropagatesDirectoryFlushFailures()
    {
        var root = MarkerTestRoot();
        try
        {
            Directory.CreateDirectory(
                Path.Combine(root, "FIRM", "reconciliation"));
            var durability = new RecordingDirectoryDurability
            {
                Failure = new IOException("directory fsync failed"),
            };
            var store = new FileReconciliationMarkerStore(
                MarkerOptions(root), durability);
            var marker = CancelMarker();

            var persistFailure = Assert.Throws<ReconciliationMarkerPersistException>(
                () => store.Persist(marker));
            Assert.False(persistFailure.DurablyPublished);
            durability.Failure = null;
            Assert.Single(store.Load());
            store.Remove(marker.Id);

            store.Persist(marker);
            durability.Failure = new IOException("delete fsync failed");
            Assert.Throws<IOException>(() => store.Remove(marker.Id));
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_WriteFailureIsNotDurablyPublished()
    {
        var root = MarkerTestRoot();
        Directory.CreateDirectory(Path.Combine(root, "FIRM", "reconciliation"));
        try
        {
            var fileOperations = new RecordingMarkerFileOperations
            {
                FailWrite = true,
            };
            var store = new FileReconciliationMarkerStore(
                MarkerOptions(root),
                new RecordingDirectoryDurability(),
                fileOperations);

            var failure = Assert.Throws<ReconciliationMarkerPersistException>(
                () => store.Persist(CancelMarker()));

            Assert.False(failure.DurablyPublished);
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                Path.Combine(root, "FIRM", "reconciliation")));
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_StagingDirectoryFsyncFailureIsNotKnownDurable()
    {
        var root = MarkerTestRoot();
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            var durability = new RecordingDirectoryDurability
            {
                Failure = new IOException("staging fsync failed"),
                FailOnCall = 1,
            };
            var store = new FileReconciliationMarkerStore(
                MarkerOptions(root), durability);

            var failure = Assert.Throws<ReconciliationMarkerPersistException>(
                () => store.Persist(CancelMarker()));

            Assert.False(failure.DurablyPublished);
            Assert.Single(Directory.EnumerateFiles(
                directory, "*.json.writing"));
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_RenameFailureReportsDurableStaging()
    {
        var root = MarkerTestRoot();
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            var fileOperations = new RecordingMarkerFileOperations
            {
                FailMove = true,
            };
            var store = new FileReconciliationMarkerStore(
                MarkerOptions(root),
                new RecordingDirectoryDurability(),
                fileOperations);

            var failure = Assert.Throws<ReconciliationMarkerPersistException>(
                () => store.Persist(CancelMarker()));

            Assert.True(failure.DurablyPublished);
            Assert.Equal(CancelMarker(), Assert.Single(store.Load()));
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_FinalDirectoryFsyncFailureReportsDurableMarker()
    {
        var root = MarkerTestRoot();
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            var durability = new RecordingDirectoryDurability
            {
                Failure = new IOException("final fsync failed"),
                FailOnCall = 2,
            };
            var store = new FileReconciliationMarkerStore(
                MarkerOptions(root), durability);

            var failure = Assert.Throws<ReconciliationMarkerPersistException>(
                () => store.Persist(CancelMarker()));

            Assert.True(failure.DurablyPublished);
            Assert.Equal(CancelMarker(), Assert.Single(store.Load()));
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_ValidStagingOnlyRecoversAndForcesDrain()
    {
        var root = MarkerTestRoot();
        var options = MarkerOptions(root);
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            var marker = CancelMarker();
            WriteMarkerArtifact(
                Path.Combine(directory, marker.Id + ".json.writing"), marker);
            var store = new FileReconciliationMarkerStore(
                options, new RecordingDirectoryDurability());
            var pending = new PendingCancelRegistry();
            Assert.True(pending.TryAdd(
                marker.OriginalClOrdId, marker.MutationClOrdId));
            var ownership = new OrderOwnershipMap();
            ownership.Register(marker.OriginalClOrdId, Owner);
            ownership.RegisterCancelLink(
                marker.MutationClOrdId, marker.OriginalClOrdId);
            var drain = new NeverDrainController();
            var recovery = CreateMarkerRecovery(
                store,
                new EventDispatcher(new NullEventStore()),
                pending,
                new PendingReplacementRegistry(),
                ownership,
                new ClOrdIdPrefixRegistry(),
                drain);

            Assert.Equal(1, recovery.Apply());
            Assert.True(drain.IsDraining);
            Assert.Equal(0, pending.CountForTesting);
            Assert.Single(store.Load());
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_CorruptStagingForcesDrain()
    {
        var root = MarkerTestRoot();
        var options = MarkerOptions(root);
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "CancelPreSend-200.json.writing"),
                "{\"kind\":");
            var drain = new NeverDrainController();
            var recovery = CreateMarkerRecovery(
                new FileReconciliationMarkerStore(
                    options, new RecordingDirectoryDurability()),
                new EventDispatcher(new NullEventStore()),
                new PendingCancelRegistry(),
                new PendingReplacementRegistry(),
                new OrderOwnershipMap(),
                new ClOrdIdPrefixRegistry(),
                drain);

            Assert.Equal(1, recovery.Apply());
            Assert.True(drain.IsDraining);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_DuplicateEqualArtifactsChooseFinalAndDurablyCleanupStaging()
    {
        var root = MarkerTestRoot();
        var options = MarkerOptions(root);
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            var durability = new RecordingDirectoryDurability();
            var store = new FileReconciliationMarkerStore(options, durability);
            var marker = CancelMarker();
            store.Persist(marker);
            var final = Path.Combine(directory, marker.Id + ".json");
            var staging = final + ".writing";
            File.Copy(final, staging);
            var callsBeforeLoad = durability.Calls;

            Assert.Equal(marker, Assert.Single(store.Load()));
            Assert.False(File.Exists(staging));
            Assert.Equal(callsBeforeLoad + 1, durability.Calls);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_ConflictingDuplicatesForceDrain()
    {
        var root = MarkerTestRoot();
        var options = MarkerOptions(root);
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new FileReconciliationMarkerStore(
                options, new RecordingDirectoryDurability());
            var marker = CancelMarker();
            store.Persist(marker);
            WriteMarkerArtifact(
                Path.Combine(directory, marker.Id + ".json.writing"),
                marker with { OwnerEndClientId = "different-owner" });
            var drain = new NeverDrainController();

            Assert.Equal(1, CreateMarkerRecovery(
                store,
                new EventDispatcher(new NullEventStore()),
                new PendingCancelRegistry(),
                new PendingReplacementRegistry(),
                new OrderOwnershipMap(),
                new ClOrdIdPrefixRegistry(),
                drain).Apply());
            Assert.True(drain.IsDraining);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void FileReconciliationMarkerStore_UnexpectedArtifactForcesDrain()
    {
        var root = MarkerTestRoot();
        var options = MarkerOptions(root);
        var directory = Path.Combine(root, "FIRM", "reconciliation");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "ignored.tmp"), "unexpected");
            var drain = new NeverDrainController();

            Assert.Equal(1, CreateMarkerRecovery(
                new FileReconciliationMarkerStore(
                    options, new RecordingDirectoryDurability()),
                new EventDispatcher(new NullEventStore()),
                new PendingCancelRegistry(),
                new PendingReplacementRegistry(),
                new OrderOwnershipMap(),
                new ClOrdIdPrefixRegistry(),
                drain).Apply());
            Assert.True(drain.IsDraining);
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public void StartupMarkerWithoutRequestStillBurnsMutationIdBeforeClearingSafePreSendMarker()
    {
        var markers = new InMemoryReconciliationMarkerStore();
        markers.Persist(new ReconciliationMarker(
            ReconciliationMarkerKind.CancelPreSend,
            OriginalClOrdId: 100,
            MutationClOrdId: 200,
            OwnerEndClientId: Owner.Value));
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var drain = new NeverDrainController();
        var recovery = CreateMarkerRecovery(
            markers,
            new EventDispatcher(new NullEventStore()),
            new PendingCancelRegistry(),
            new PendingReplacementRegistry(),
            new OrderOwnershipMap(),
            clOrdIds,
            drain);

        Assert.Equal(0, recovery.Apply());
        Assert.False(drain.IsDraining);
        Assert.Empty(markers.Load());
        Assert.Equal(201UL, clOrdIds.Generate(Owner));
    }

    [Fact]
    public void StartupAmbiguousMarkerWithoutRequestFailsClosed()
    {
        var markers = new InMemoryReconciliationMarkerStore();
        markers.Persist(new ReconciliationMarker(
            ReconciliationMarkerKind.ReplaceAmbiguous,
            OriginalClOrdId: 100,
            MutationClOrdId: 200,
            OwnerEndClientId: Owner.Value,
            NewRemainingNotional: 3100m,
            AmbiguousAtUtc: DateTimeOffset.UtcNow));
        var drain = new NeverDrainController();
        var recovery = CreateMarkerRecovery(
            markers,
            new EventDispatcher(new NullEventStore()),
            new PendingCancelRegistry(),
            new PendingReplacementRegistry(),
            new OrderOwnershipMap(),
            new ClOrdIdPrefixRegistry(),
            drain);

        Assert.Equal(1, recovery.Apply());
        Assert.True(drain.IsDraining);
        Assert.Single(markers.Load());
    }

    [Fact]
    public async Task QuantityOnlyModify_InheritsOriginalPriceAcrossRiskWalAndGateway()
    {
        var gateway = new TestGateway();
        var harness = BuildModify(gateway);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, NewPrice: null),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        Assert.Equal(30m, gateway.LastReplacePrice);
        Assert.Equal(30m, Assert.Single(harness.Replacements.Snapshot()).Intent.NewPrice);
    }

    [Fact]
    public async Task DuplicateCancel_ReturnsExistingMutation_WithoutAllocatingOrSendingAgain()
    {
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var gateway = new TestGateway();
        var pending = new PendingCancelRegistry();
        var service = BuildCancel(clOrdIds, gateway, pending);

        var first = await service.CancelAsync(Owner, 100, CancellationToken.None);
        var duplicate = await service.CancelAsync(Owner, 100, CancellationToken.None);
        var nextAllocated = clOrdIds.Generate(Owner);

        Assert.Equal(OrderCancelResultKind.Accepted, first.Kind);
        Assert.Equal(first.CancelClOrdId, duplicate.CancelClOrdId);
        Assert.Equal(first.CancelClOrdId + 1, nextAllocated);
        Assert.Equal(1, gateway.CancelCalls);
    }

    [Fact]
    public async Task PreSendCancelFailure_ResolvesIntent_AndRetryAllocatesAndSendsFreshMutation()
    {
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var pending = new PendingCancelRegistry();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var botMappings = new InMemoryUserBotOrderMappingRegistry();
        var credentialId = Guid.NewGuid();
        var firstService = new OrderCancelService(
            clOrdIds, ownership, book, new UnavailableExchangeGateway(), dispatcher,
            NullLogger<OrderCancelService>.Instance, botMappings, pending);

        var failed = await firstService.CancelAsync(
            Owner, 100, CancellationToken.None,
            new BotOrigin(credentialId, ExternalClOrdId: 77));

        Assert.Equal(OrderCancelResultKind.GatewayFailed, failed.Kind);
        Assert.Equal(0, pending.CountForTesting);
        Assert.False(ownership.TryResolveOrig(failed.CancelClOrdId, out _));
        Assert.False(ownership.TryResolve(failed.CancelClOrdId, out _));
        Assert.False(botMappings.TryGetCancelMapping(failed.CancelClOrdId, out _));
        Assert.Collection(store.Events,
            static e => Assert.IsType<OrderCancelRequestedEvent>(e),
            static e => Assert.IsType<OrderCancelPreSendFailedEvent>(e));

        var retryGateway = new TestGateway();
        var retryService = new OrderCancelService(
            clOrdIds, ownership, book, retryGateway, dispatcher,
            NullLogger<OrderCancelService>.Instance, botMappings, pending);
        var retry = await retryService.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.Accepted, retry.Kind);
        Assert.Equal(failed.CancelClOrdId + 1, retry.CancelClOrdId);
        Assert.Equal(1, retryGateway.CancelCalls);
    }

    [Fact]
    public async Task PreSendCancelResolution_ReplayAllowsFreshRetry_AndAckWithoutOrigRoutes()
    {
        var store = new RecordingEventStore();
        var sourcePending = new PendingCancelRegistry();
        var sourceOwnership = new OrderOwnershipMap();
        var sourceBook = new WorkingOrderBook();
        Assert.True(sourceBook.TryAdd(Working()));
        sourceOwnership.Register(100, Owner);
        var sourceService = new OrderCancelService(
            new ClOrdIdPrefixRegistry(), sourceOwnership, sourceBook,
            new UnavailableExchangeGateway(), new EventDispatcher(store),
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: sourcePending);
        var failed = await sourceService.CancelAsync(Owner, 100, CancellationToken.None);
        Assert.Equal(OrderCancelResultKind.GatewayFailed, failed.Kind);

        var recoveredPending = new PendingCancelRegistry();
        var recovered = BuildRecoveryState(pendingCancels: recoveredPending);
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        Assert.Equal(0, recoveredPending.CountForTesting);
        Assert.False(recovered.Ownership.TryResolveOrig(failed.CancelClOrdId, out _));

        var gateway = new TestGateway();
        var retryService = new OrderCancelService(
            recovered.ClOrdIds, recovered.Ownership, recovered.Book, gateway,
            new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: recoveredPending);
        var retry = await retryService.CancelAsync(Owner, 100, CancellationToken.None);
        Assert.Equal(OrderCancelResultKind.Accepted, retry.Kind);
        Assert.Equal(failed.CancelClOrdId + 1, retry.CancelClOrdId);
        Assert.Equal(1, gateway.CancelCalls);

        var processor = new ExecutionReportProcessor(
            recovered.Ownership, recovered.Book, new PositionKeeper(), new RecordingSink(),
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance,
            pendingCancels: recoveredPending);
        processor.Apply(
            retry.CancelClOrdId, ExecKind.Canceled,
            leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m,
            rejectReason: null);
        Assert.True(recovered.Book.TryGet(100, out var original));
        Assert.Equal(OrderStatus.Cancelled, original!.Status);
    }

    [Fact]
    public async Task PreSendCancelResolutionWalFault_DrainsAndDoesNotStrandLivePendingState()
    {
        var store = new RecordingEventStore(
            failOnAppend: 2,
            failureFactory: static () => new WalFaultedException(
                "cancel resolution writer faulted", new IOException("disk full")));
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var pending = new PendingCancelRegistry();
        var drain = new NeverDrainController();
        var service = new OrderCancelService(
            clOrdIds, ownership, book, new UnavailableExchangeGateway(),
            new EventDispatcher(store), NullLogger<OrderCancelService>.Instance,
            pendingCancels: pending, reconciliationDrain: drain);

        var result = await service.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.ReconciliationRequired, result.Kind);
        Assert.Equal("pre_send_cancel_resolution_not_durable", result.Reason);
        Assert.True(drain.IsDraining);
        Assert.Equal(0, pending.CountForTesting);
        Assert.False(ownership.TryResolveOrig(result.CancelClOrdId, out _));
        Assert.Single(store.Events);
        Assert.IsType<OrderCancelRequestedEvent>(store.Events[0]);
        var snapshot = CreateSnapshotter(book, ownership, pending).Capture(seq: 1);
        Assert.Empty(snapshot.PendingCancels);

        var blockedRetry = await service.CancelAsync(Owner, 100, CancellationToken.None);
        Assert.Equal(OrderCancelResultKind.ReconciliationRequired, blockedRetry.Kind);
        Assert.Equal(result.CancelClOrdId + 1, clOrdIds.Generate(Owner));
    }

    [Fact]
    public async Task PreSendCancelResolutionTransientBackpressure_RetriesAndFlushes()
    {
        var markers = new InMemoryReconciliationMarkerStore();
        var store = new RecordingEventStore(
            failOnAppend: 2,
            failureFactory: static () => new WalBackpressureException("transient"),
            failureCount: 1);
        var dispatcher = new EventDispatcher(store);
        var pending = new PendingCancelRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var drain = new NeverDrainController();
        var service = new OrderCancelService(
            new ClOrdIdPrefixRegistry(), ownership, book,
            new UnavailableExchangeGateway(), dispatcher,
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: pending,
            reconciliationDrain: drain,
            resolutionWriter: new ReconciliationResolutionWriter(
                markers, dispatcher,
                NullLogger<ReconciliationResolutionWriter>.Instance));

        var result = await service.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.GatewayFailed, result.Kind);
        Assert.False(drain.IsDraining);
        Assert.Equal(0, pending.CountForTesting);
        Assert.Empty(markers.Load());
        Assert.Equal(2, store.Events.Count);
        Assert.True(store.FlushCalls > 0);
    }

    [Fact]
    public async Task CancelPreSend_WhenMarkerAndWalBothFail_RetainsPendingIntentAcrossRestart()
    {
        var markers = new FaultingMarkerStore(durablyPublished: false);
        var store = PermanentResolutionFaultStore();
        var dispatcher = new EventDispatcher(store);
        var pending = new PendingCancelRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var drain = new NeverDrainController();
        var service = new OrderCancelService(
            new ClOrdIdPrefixRegistry(), ownership, book,
            new UnavailableExchangeGateway(), dispatcher,
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: pending,
            reconciliationDrain: drain,
            resolutionWriter: new ReconciliationResolutionWriter(
                markers, dispatcher,
                NullLogger<ReconciliationResolutionWriter>.Instance));

        var result = await service.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.ReconciliationRequired, result.Kind);
        Assert.True(drain.IsDraining);
        Assert.True(pending.TryGetByCancel(result.CancelClOrdId, out _));
        Assert.True(ownership.TryResolveOrig(result.CancelClOrdId, out _));
        Assert.Empty(markers.Load());

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        Assert.True(recovered.PendingCancels.TryGetByCancel(
            result.CancelClOrdId, out _));
        var startupDrain = new NeverDrainController();
        Assert.Equal(1, CreateColdStartGuard(
            recovered.PendingCancels, recovered.Replacements,
            startupDrain).Apply());
        Assert.True(startupDrain.IsDraining);
        Assert.True(recovered.PendingCancels.TryGetByCancel(
            result.CancelClOrdId, out _));
        Assert.True(recovered.Ownership.TryResolveOrig(
            result.CancelClOrdId, out _));
    }

    [Fact]
    public async Task OrdinaryAmbiguousReplace_RestartFailsClosedWithoutBlindResend()
    {
        var store = new RecordingEventStore();
        var gateway = new TestGateway
        {
            ReplaceException = new IOException("wire outcome unknown"),
        };
        var runtime = BuildModify(gateway, store);
        Assert.Equal(
            OrderModifyResultKind.GatewayAmbiguous,
            (await runtime.Service.ModifyAsync(
                new OrderModifyRequest(Owner, 100, 120, 31m),
                CancellationToken.None)).Kind);

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        Assert.True(Assert.Single(
            recovered.Replacements.Snapshot()).AmbiguousMarginHeld);
        var drain = new NeverDrainController();

        Assert.Equal(1, CreateColdStartGuard(
            recovered.PendingCancels, recovered.Replacements, drain).Apply());
        Assert.True(drain.IsDraining);
        Assert.Equal(1, gateway.ReplaceCalls);
    }

    [Fact]
    public async Task CleanResolvedWal_RestartDoesNotTriggerColdStartFence()
    {
        var store = new RecordingEventStore();
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var runtime = BuildModify(gateway, store);
        Assert.Equal(
            OrderModifyResultKind.GatewayFailed,
            (await runtime.Service.ModifyAsync(
                new OrderModifyRequest(Owner, 100, 120, 31m),
                CancellationToken.None)).Kind);

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);
        var drain = new NeverDrainController();

        Assert.Equal(0, CreateColdStartGuard(
            recovered.PendingCancels, recovered.Replacements, drain).Apply());
        Assert.False(drain.IsDraining);
    }

    [Fact]
    public void SnapshotPendingCancelResolvedByWalTail_DoesNotTriggerColdStartFence()
    {
        var recovered = BuildRecoveryState(
            pendingCancels: new PendingCancelRegistry());
        Assert.True(recovered.PendingCancels.TryAdd(100, 200));
        recovered.Ownership.RegisterCancelLink(200, 100);
        recovered.Replayer.Apply(new OrderCancelPreSendFailedEvent
        {
            CancelClOrdId = 200,
            OriginalClOrdId = 100,
            OwnerEndClientId = Owner.Value,
            Reason = "gateway_unavailable",
        });
        var drain = new NeverDrainController();

        Assert.Equal(0, CreateColdStartGuard(
            recovered.PendingCancels, recovered.Replacements, drain).Apply());
        Assert.False(drain.IsDraining);
        Assert.Equal(0, recovered.PendingCancels.CountForTesting);
        Assert.False(recovered.Ownership.TryResolveOrig(200, out _));
    }

    [Fact]
    public async Task PersistenceRecovery_UnresolvedWalIntentBeginsDrainBeforeReadiness()
    {
        var root = MarkerTestRoot();
        try
        {
            var store = new RecordingEventStore();
            store.Append(new OrderCancelRequestedEvent
            {
                CancelClOrdId = 200,
                OriginalClOrdId = 100,
                OwnerEndClientId = Owner.Value,
            });
            var recovered = BuildRecoveryState();
            var drain = new NeverDrainController();
            var guard = CreateColdStartGuard(
                recovered.PendingCancels, recovered.Replacements, drain);
            var recovery = new PersistenceRecovery(
                store,
                CreateSnapshotter(
                    recovered.Book,
                    recovered.Ownership,
                    recovered.PendingCancels),
                recovered.Replayer,
                new SnapshotStore(root, "FIRM"),
                NullLogger<PersistenceRecovery>.Instance,
                coldStartLifecycleGuard: guard);

            await recovery.RunAsync();

            Assert.True(drain.IsDraining);
            Assert.Equal(
                "cold_start_unresolved_lifecycle_intents",
                drain.Reason);
            Assert.True(recovered.PendingCancels.TryGetByCancel(200, out _));
        }
        finally
        {
            DeleteMarkerTestRoot(root);
        }
    }

    [Fact]
    public async Task ReplayedPendingCancel_MakesRetryIdempotentWithoutGatewayCall()
    {
        var pending = new PendingCancelRegistry();
        var recovered = BuildRecoveryState(pendingCancels: pending);
        recovered.Replayer.Apply(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 200,
            OriginalClOrdId = 100,
            OwnerEndClientId = Owner.Value,
        });
        var gateway = new TestGateway();
        var service = new OrderCancelService(
            recovered.ClOrdIds, recovered.Ownership, recovered.Book, gateway,
            new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: pending);

        var retry = await service.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.Accepted, retry.Kind);
        Assert.Equal(200UL, retry.CancelClOrdId);
        Assert.Equal(0, gateway.CancelCalls);
    }

    [Fact]
    public void PendingCancelSnapshot_RestoresIdempotencyKey()
    {
        var original = new PendingCancelRegistry();
        Assert.True(original.TryAdd(100, 200));
        var restored = new PendingCancelRegistry();

        restored.Restore(original.Snapshot());
        var claim = restored.Claim(100);

        Assert.False(claim.IsAcquired);
        Assert.Equal(200UL, claim.ExistingCancelClOrdId);
    }

    [Fact]
    public async Task CancelOnlySnapshot_RestoresRetryIdempotencyAndAckRouting()
    {
        var sourceBook = new WorkingOrderBook();
        Assert.True(sourceBook.TryAdd(Working()));
        var sourceOwnership = new OrderOwnershipMap();
        sourceOwnership.Register(100, Owner);
        var sourcePending = new PendingCancelRegistry();
        Assert.True(sourcePending.TryAdd(100, 200));
        sourceOwnership.RegisterCancelLink(200, 100);
        var snapshot = CreateSnapshotter(
            sourceBook, sourceOwnership, sourcePending).Capture(seq: 5);
        Assert.Empty(snapshot.PendingReplacements);
        Assert.Single(snapshot.PendingCancels);

        var restoredBook = new WorkingOrderBook();
        var restoredOwnership = new OrderOwnershipMap();
        var restoredPending = new PendingCancelRegistry();
        CreateSnapshotter(restoredBook, restoredOwnership, restoredPending).Restore(snapshot);

        var gateway = new TestGateway();
        var retryService = new OrderCancelService(
            new ClOrdIdPrefixRegistry(), restoredOwnership, restoredBook, gateway,
            new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: restoredPending);
        var retry = await retryService.CancelAsync(Owner, 100, CancellationToken.None);
        Assert.Equal(OrderCancelResultKind.Accepted, retry.Kind);
        Assert.Equal(200UL, retry.CancelClOrdId);
        Assert.Equal(0, gateway.CancelCalls);
        Assert.True(restoredOwnership.TryResolveOrig(200, out var linked));
        Assert.Equal(100UL, linked);

        var processor = new ExecutionReportProcessor(
            restoredOwnership, restoredBook, new PositionKeeper(), new RecordingSink(),
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance,
            pendingCancels: restoredPending);
        processor.Apply(
            clOrdId: 200,
            kind: ExecKind.Canceled,
            leaves: 0,
            cumQty: 0,
            lastQty: 0,
            lastPx: 0m,
            rejectReason: null);

        Assert.True(restoredBook.TryGet(100, out var restoredOrder));
        Assert.Equal(OrderStatus.Cancelled, restoredOrder!.Status);
        Assert.Equal(0, restoredPending.CountForTesting);
    }

    [Fact]
    public void CancelReject_ResolvesPendingIntent_WithoutRejectingOriginalOrder()
    {
        var pending = new PendingCancelRegistry();
        Assert.True(pending.TryAdd(100, 200));
        var botMappings = new InMemoryUserBotOrderMappingRegistry();
        var credentialId = Guid.NewGuid();
        botMappings.RegisterCancelInternal(200, 100, credentialId, 300);
        var recovered = BuildRecoveryState(
            pendingCancels: pending,
            botMappings: botMappings);
        recovered.Ownership.RegisterCancelLink(200, 100);

        recovered.Replayer.Apply(new ExecutionReportReceivedEvent
        {
            ClOrdId = 200,
            OrigClOrdId = 100,
            ExecKind = nameof(ExecKind.Rejected),
            LeavesQuantity = 100,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0m,
            RejectReason = "too_late_to_cancel",
            Synthetic = false,
        });

        Assert.True(recovered.Book.TryGet(100, out var order));
        Assert.Equal(OrderStatus.Working, order!.Status);
        Assert.Equal(0, pending.CountForTesting);
        Assert.False(recovered.Ownership.TryResolveOrig(200, out _));
        Assert.False(botMappings.TryGetCancelMapping(200, out _));
    }

    [Fact]
    public async Task TerminalCancel_IsRejectedWithoutAllocationOrGatewayCall()
    {
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var gateway = new TestGateway();
        var pending = new PendingCancelRegistry();
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var order = Working();
        order.MarkCancelled();
        Assert.True(book.TryAdd(order));
        ownership.Register(order.ClOrdId, Owner);
        var service = new OrderCancelService(
            clOrdIds, ownership, book, gateway, new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance, pendingCancels: pending);

        var result = await service.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.Conflict, result.Kind);
        Assert.Equal(0, gateway.CancelCalls);
        Assert.Equal(1UL, clOrdIds.Generate(Owner));
    }

    [Theory]
    [InlineData(OrderType.Limit, null)]
    [InlineData(OrderType.Limit, -1d)]
    [InlineData(OrderType.Market, 30d)]
    [InlineData(OrderType.StopLoss, 30d)]
    public async Task ApplicationSubmit_RejectsInvalidPriceTypeFromNonRestIngress(
        OrderType type,
        double? price)
    {
        var gateway = new TestGateway();
        var book = new WorkingOrderBook();
        var submitter = new OrderSubmissionService(
            new ClOrdIdPrefixRegistry(), new OrderOwnershipMap(), book, gateway,
            new RecordingSink(), new RiskPipeline(Array.Empty<IRiskCheck>()),
            new NoOpMarginProvider(), new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            new EventDispatcher(new NullEventStore()), new NeverDrainController(),
            NullLogger<OrderSubmissionService>.Instance);

        var result = await submitter.SubmitAsync(
            new OrderSubmissionRequest(
                Owner, "FIRM", "PETR4", 4321, OrderSide.Buy, type, 100,
                price.HasValue ? (decimal)price.Value : null),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.BadRequest, result.Kind);
        Assert.Equal(0, gateway.SubmitCalls);
        Assert.Empty(book.Snapshot());
    }

    private static ModifyHarness BuildModify(
        IExchangeGateway gateway,
        IEventStore? store = null,
        IReconciliationMarkerStore? markerStore = null)
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var replacements = new PendingReplacementRegistry();
        var margin = new NoOpReplaceMargin();
        var drain = new NeverDrainController();
        var dispatcher = new EventDispatcher(store ?? new NullEventStore());
        var markers = markerStore ?? new InMemoryReconciliationMarkerStore();
        var resolutionWriter = new ReconciliationResolutionWriter(
            markers, dispatcher,
            NullLogger<ReconciliationResolutionWriter>.Instance);
        var service = new OrderModifyService(
            new ClOrdIdPrefixRegistry(), ownership, book, gateway, new RecordingSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()), margin,
            replacements, dispatcher,
            drain, NullLogger<OrderModifyService>.Instance,
            resolutionWriter: resolutionWriter);
        return new ModifyHarness(service, replacements, margin, drain, markers, dispatcher);
    }

    private static OrderCancelService BuildCancel(
        ClOrdIdPrefixRegistry clOrdIds,
        TestGateway gateway,
        PendingCancelRegistry pending)
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        return new OrderCancelService(
            clOrdIds, ownership, book, gateway, new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance, pendingCancels: pending);
    }

    private static RecoveryHarness BuildRecoveryState(
        PendingCancelRegistry? pendingCancels = null,
        IUserBotOrderMappingRegistry? botMappings = null)
    {
        pendingCancels ??= new PendingCancelRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var replacements = new PendingReplacementRegistry();
        var margin = new NoOpReplaceMargin();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var processor = new ExecutionReportProcessor(
            ownership, book, new PositionKeeper(), new RecordingSink(),
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance,
            replacements: replacements, replaceMargin: margin,
            pendingCancels: pendingCancels, botMappings: botMappings);
        var replayer = new EventReplayer(
            book, ownership, new KillSwitchService(), new SymbolHaltService(),
            new SessionPhaseService(), processor, new AlgoBook(), clOrdIds,
            new AlgoIdRegistry(), replacements: replacements,
            replaceMargin: margin, pendingCancels: pendingCancels);
        return new RecoveryHarness(
            replayer, replacements, pendingCancels, clOrdIds, ownership, book,
            dispatcher);
    }

    private static ReconciliationMarkerRecovery CreateMarkerRecovery(
        IReconciliationMarkerStore markers,
        EventDispatcher dispatcher,
        PendingCancelRegistry pendingCancels,
        PendingReplacementRegistry replacements,
        OrderOwnershipMap ownership,
        ClOrdIdPrefixRegistry clOrdIds,
        NeverDrainController drain) =>
        new(
            markers, dispatcher, pendingCancels, replacements, ownership,
            clOrdIds, drain,
            NullLogger<ReconciliationMarkerRecovery>.Instance);

    private static ColdStartLifecycleGuard CreateColdStartGuard(
        PendingCancelRegistry pendingCancels,
        PendingReplacementRegistry replacements,
        NeverDrainController drain) =>
        new(
            pendingCancels,
            replacements,
            drain,
            NullLogger<ColdStartLifecycleGuard>.Instance);

    private static RecordingEventStore PermanentResolutionFaultStore() =>
        new(
            failOnAppend: 2,
            failureFactory: static () => new WalFaultedException(
                "resolution fault", new IOException("disk full")));

    private static string MarkerTestRoot() =>
        Path.Combine(
            Environment.CurrentDirectory,
            "TestResults",
            "reconciliation-markers-" + Guid.NewGuid().ToString("N"));

    private static PersistenceOptions MarkerOptions(string root) =>
        new()
        {
            DataDirectory = root,
            FirmId = "FIRM",
        };

    private static ReconciliationMarker CancelMarker() =>
        new(
            ReconciliationMarkerKind.CancelPreSend,
            OriginalClOrdId: 100,
            MutationClOrdId: 200,
            OwnerEndClientId: Owner.Value);

    private static void WriteMarkerArtifact(
        string path,
        ReconciliationMarker marker) =>
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                marker,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static void DeleteMarkerTestRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static Order Working()
    {
        var order = new Order(
            100, Owner, "PETR4", 4321, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        order.MarkWorking();
        return order;
    }

    private static StateSnapshotter CreateSnapshotter(
        WorkingOrderBook book,
        OrderOwnershipMap ownership,
        PendingCancelRegistry pendingCancels) =>
        new(
            book,
            new PositionKeeper(),
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(),
            ownership,
            new AlgoBook(),
            new AlgoIdRegistry(),
            new CashLedger(),
            replacements: new PendingReplacementRegistry(),
            pendingCancels: pendingCancels);

    private sealed record ModifyHarness(
        OrderModifyService Service,
        PendingReplacementRegistry Replacements,
        NoOpReplaceMargin Margin,
        NeverDrainController Drain,
        IReconciliationMarkerStore Markers,
        EventDispatcher Dispatcher);

    private sealed record RecoveryHarness(
        EventReplayer Replayer,
        PendingReplacementRegistry Replacements,
        PendingCancelRegistry PendingCancels,
        ClOrdIdPrefixRegistry ClOrdIds,
        OrderOwnershipMap Ownership,
        WorkingOrderBook Book,
        EventDispatcher Dispatcher);

    private sealed class TestGateway : IExchangeGateway
    {
        public int SubmitCalls;
        public int CancelCalls;
        public int ReplaceCalls;
        public bool BlockReplace;
        public Exception? ReplaceException;
        public decimal? LastReplacePrice;
        public TaskCompletionSource ReplaceEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReplace { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SubmitAsync(Order order, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SubmitCalls);
            return Task.CompletedTask;
        }

        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CancelCalls);
            return Task.CompletedTask;
        }

        public async Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReplaceCalls);
            LastReplacePrice = newPrice;
            ReplaceEntered.TrySetResult();
            if (BlockReplace)
                await ReleaseReplace.Task.WaitAsync(cancellationToken);
            if (ReplaceException is not null)
                throw ReplaceException;
        }
    }

    private sealed class NoOpReplaceMargin : IReplaceMarginCoordinator
    {
        public List<ulong> Aborted { get; } = new();

        public Task<RiskDecision> PrepareReplaceAsync(
            ulong originalClOrdId,
            ulong newClOrdId,
            EndClientId owner,
            decimal newRemainingNotional,
            CancellationToken ct) => Task.FromResult(RiskDecision.Approve);

        public void CommitReplace(ulong originalClOrdId, ulong newClOrdId, decimal newRemainingNotional) { }
        public void AbortReplace(ulong newClOrdId) => Aborted.Add(newClOrdId);
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private sealed class NeverDrainController : Lifecycle.IDrainController
    {
        public bool IsDraining { get; private set; }
        public string? Reason { get; private set; }
        public void BeginDrain(string reason)
        {
            IsDraining = true;
            Reason = reason;
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private long _seq;
        private readonly int? _failOnAppend;
        private readonly Func<Exception>? _failureFactory;
        private int _failuresRemaining;
        public int FlushCalls { get; private set; }
        public List<WalEvent> Events { get; } = new();
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public RecordingEventStore(
            int? failOnAppend = null,
            Func<Exception>? failureFactory = null,
            int failureCount = int.MaxValue)
        {
            _failOnAppend = failOnAppend;
            _failureFactory = failureFactory;
            _failuresRemaining = failureCount;
        }

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            var appendNumber = checked((int)CurrentSeq + 1);
            if (_failOnAppend == appendNumber && _failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw _failureFactory?.Invoke()
                    ?? new WalBackpressureException("configured append failure");
            }
            lock (Events)
                Events.Add(evt);
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default)
        {
            FlushCalls++;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            WalEvent[] snapshot;
            lock (Events)
                snapshot = Events.ToArray();
            for (var i = (int)sinceSeqExclusive; i < snapshot.Length; i++)
                yield return (i + 1, snapshot[i]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDirectoryDurability
        : IReconciliationDirectoryDurability
    {
        public int Calls { get; private set; }
        public List<string> Paths { get; } = new();
        public Exception? Failure { get; set; }
        public int? FailOnCall { get; set; }
        public Action<string>? OnFlush { get; set; }

        public void Flush(string directoryPath)
        {
            Calls++;
            Paths.Add(directoryPath);
            OnFlush?.Invoke(directoryPath);
            if (Failure is not null
                && (FailOnCall is null || FailOnCall == Calls))
                throw Failure;
        }
    }

    private sealed class RecordingMarkerFileOperations
        : IReconciliationMarkerFileOperations
    {
        public bool FailWrite { get; set; }
        public bool FailMove { get; set; }

        public void WriteAndFlush(string path, byte[] payload)
        {
            if (FailWrite)
                throw new IOException("staging file flush failed");
            using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        public void Move(string source, string destination)
        {
            if (FailMove)
                throw new IOException("rename failed");
            File.Move(source, destination, overwrite: true);
        }
    }

    private sealed class FaultingMarkerStore : IReconciliationMarkerStore
    {
        private readonly bool _durablyPublished;
        private ReconciliationMarker? _marker;

        public FaultingMarkerStore(bool durablyPublished) =>
            _durablyPublished = durablyPublished;

        public void Persist(ReconciliationMarker marker)
        {
            if (_durablyPublished)
                _marker = marker;
            throw new ReconciliationMarkerPersistException(
                "configured marker persistence failure",
                _durablyPublished,
                new IOException("sidecar unavailable"));
        }

        public void Remove(string markerId)
        {
            if (_marker?.Id == markerId)
                _marker = null;
        }

        public IReadOnlyList<ReconciliationMarker> Load() =>
            _marker is null
                ? Array.Empty<ReconciliationMarker>()
                : new[] { _marker! };
    }
}
