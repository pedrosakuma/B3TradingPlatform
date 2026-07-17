using System.Text.Json;
using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

public sealed class CommittedPrefixFileEventStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "committed-prefix-tests",
        Guid.NewGuid().ToString("N"));

    public CommittedPrefixFileEventStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(WalCommitBoundary.RecordAppended)]
    [InlineData(WalCommitBoundary.LogFsynced)]
    [InlineData(WalCommitBoundary.BeforeMarkerStage)]
    [InlineData(WalCommitBoundary.MarkerStagedAndFsynced)]
    internal async Task CrashBeforeMarkerPublication_DoesNotReplaySurvivor(
        WalCommitBoundary boundary)
    {
        var options = Options("pre-marker-" + boundary);
        var hooks = new ThrowAtBoundaryHooks(boundary);
        await using (var store = NewStore(options, hooks))
        {
            var seq = store.Append(NewOrder(1));
            var failure = await Assert.ThrowsAsync<WalFaultedException>(
                () => store.FlushThroughAsync(seq).AsTask());
            Assert.Same(store.TerminalFault, failure.InnerException);
            Assert.Equal(1, store.LastAdmittedSeq);
            Assert.Equal(1, store.LastAppendedSeq);
            Assert.Equal(
                boundary == WalCommitBoundary.RecordAppended ? 0 : 1,
                store.LastLogFsyncedSeq);
            Assert.Equal(0, store.LastCommittedSeq);
            Assert.False(store.IsHealthy);
        }

        await using var reopened = NewStore(options);
        Assert.Equal(0, reopened.LastCommittedSeq);
        Assert.Empty(await ReplayIds(reopened));
    }

    [Theory]
    [InlineData(WalCommitBoundary.MarkerPublished)]
    [InlineData(WalCommitBoundary.MarkerDirectoryFsynced)]
    internal async Task FaultAfterMarkerRename_FailsWaiterButRecoveredMarkerRemainsAuthoritative(
        WalCommitBoundary boundary)
    {
        var options = Options("post-publish-" + boundary);
        var hooks = new BlockingBoundaryHooks(boundary, throwOnRelease: true);
        await using (var store = NewStore(options, hooks))
        {
            var seq = store.Append(NewOrder(1));
            Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));
            var firstFence = store.FlushThroughAsync(seq).AsTask();
            var secondFence = store.FlushThroughAsync(seq).AsTask();
            hooks.Release.Set();
            await Assert.ThrowsAsync<WalFaultedException>(
                () => firstFence);
            await Assert.ThrowsAsync<WalFaultedException>(
                () => secondFence);
            Assert.Equal(0, store.LastCommittedSeq);
            Assert.False(store.IsHealthy);
        }

        await using var reopened = NewStore(options);
        Assert.Equal(1, reopened.LastCommittedSeq);
        Assert.Equal(new ulong[] { 2 }, await ReplayIds(reopened));
    }

    [Fact]
    public async Task CompleteSurvivorBeyondMarker_IsTruncatedWithoutReplay()
    {
        var options = Options("survivor");
        await using (var store = NewStore(
                         options,
                         new ThrowAtBoundaryHooks(WalCommitBoundary.LogFsynced)))
        {
            var seq = store.Append(NewOrder(4));
            await Assert.ThrowsAsync<WalFaultedException>(
                () => store.FlushThroughAsync(seq).AsTask());
        }

        var log = Directory.EnumerateFiles(
            WalRoot(options), "*.log", SearchOption.AllDirectories).Single();
        Assert.True(new FileInfo(log).Length > 0);

        await using var reopened = NewStore(options);
        Assert.Empty(await ReplayIds(reopened));
        Assert.Empty(Directory.EnumerateFiles(
            WalRoot(options), "*.log", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LinuxFsyncRecovery_DurablyRemovesEmptySurvivorDayDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var options = Options("linux-empty-survivor-day");
        options.FsyncOnFlush = true;
        var survivorDay = Path.Combine(WalRoot(options), "2026-01-01");
        await using (var store = NewStore(
                         options,
                         new ThrowAtBoundaryHooks(
                             WalCommitBoundary.LogFsynced)))
        {
            var seq = store.Append(NewOrder(0));
            await Assert.ThrowsAsync<WalFaultedException>(
                () => store.FlushThroughAsync(seq).AsTask());
        }
        Assert.True(Directory.Exists(survivorDay));

        await using (var reopened = NewStore(options))
        {
            Assert.Equal(0, reopened.LastCommittedSeq);
            Assert.Empty(await ReplayIds(reopened));
            Assert.False(Directory.Exists(survivorDay));
            Assert.True(reopened.IsHealthy);
        }

        // A second real open proves the child deletion + WAL-root directory
        // update survived the complete close/reopen sequence.
        await using var secondRestart = NewStore(options);
        Assert.Equal(0, secondRestart.LastCommittedSeq);
        Assert.False(Directory.Exists(survivorDay));
    }

    [Theory]
    [InlineData(WalCommitBoundary.RecordAppended)]
    [InlineData(WalCommitBoundary.LogFsynced)]
    internal async Task SurvivorInCommittedTail_IsTruncatedBackToPriorMarker(
        WalCommitBoundary boundary)
    {
        var options = Options("tail-survivor-" + boundary);
        await using (var store = NewStore(
                         options,
                         new ThrowAtBoundaryHooks(boundary, targetSeq: 2)))
        {
            var first = store.Append(NewOrder(0));
            await store.FlushThroughAsync(first);
            var second = store.Append(NewOrder(1));
            await Assert.ThrowsAsync<WalFaultedException>(
                () => store.FlushThroughAsync(second).AsTask());
        }

        await using var reopened = NewStore(options);
        Assert.Equal(1, reopened.LastCommittedSeq);
        Assert.Equal(new ulong[] { 1 }, await ReplayIds(reopened));
    }

    [Fact]
    public async Task CorruptionBeyondMarker_IsQuarantinedByTruncationWithoutInspection()
    {
        var options = Options("corrupt-survivor");
        await using (var store = NewStore(
                         options,
                         new ThrowAtBoundaryHooks(WalCommitBoundary.RecordAppended)))
        {
            var seq = store.Append(NewOrder(6));
            await Assert.ThrowsAsync<WalFaultedException>(
                () => store.FlushThroughAsync(seq).AsTask());
        }

        var log = Directory.EnumerateFiles(
            WalRoot(options), "*.log", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(log);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(log, bytes);

        await using var reopened = NewStore(options);
        Assert.Empty(await ReplayIds(reopened));
    }

    [Fact]
    public async Task CorruptionAtOrBelowMarker_FailsStartupClosed()
    {
        var options = Options("committed-corruption");
        await using (var store = NewStore(options))
        {
            store.Append(NewOrder(0));
            await store.FlushAsync();
        }

        var log = Directory.EnumerateFiles(
            WalRoot(options), "*.log", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(log);
        bytes[SegmentWriter.RecordHeaderBytes + 1] ^= 0xff;
        File.WriteAllBytes(log, bytes);

        Assert.Throws<WalRecoveryException>(() => NewStore(options));
    }

    [Fact]
    public async Task SegmentFromDifferentGeneration_FailsStartupClosed()
    {
        var options = Options("wrong-generation");
        await using (var store = NewStore(options))
        {
            store.Append(NewOrder(0));
            await store.FlushAsync();
        }

        var metadata = Directory.EnumerateFiles(
            WalRoot(options),
            "*.log.firstseq",
            SearchOption.AllDirectories).Single();
        File.WriteAllBytes(metadata, SegmentMetadata.Encode(Guid.NewGuid(), 1));

        Assert.Throws<WalRecoveryException>(() => NewStore(options));
    }

    [Fact]
    public async Task SymlinkInsideWalTree_IsRejected()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var options = Options("symlink");
        await using (var store = NewStore(options))
            await store.FlushAsync();

        var target = Path.Combine(options.DataDirectory, "symlink-target");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(
            Path.Combine(WalRoot(options), "2026-01-01"),
            target);

        Assert.Throws<WalRecoveryException>(() => NewStore(options));
    }

    [Fact]
    public async Task ConcurrentFences_ProveOneMonotonicCommittedPrefix()
    {
        var options = Options("concurrent");
        var hooks = new BlockingBoundaryHooks(WalCommitBoundary.BeforeMarkerStage);
        await using var store = NewStore(options, hooks);
        store.Append(NewOrder(0));
        store.Append(NewOrder(1));
        store.Append(NewOrder(2));
        Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));

        var first = store.FlushThroughAsync(1).AsTask();
        var second = store.FlushThroughAsync(2).AsTask();
        var third = store.FlushThroughAsync(3).AsTask();
        hooks.Release.Set();

        await Task.WhenAll(first, second, third);
        Assert.Equal(3, store.LastCommittedSeq);
    }

    [Fact]
    public async Task ClassOCaller_SnapshotIncludesProjectionBeforeCommittedPrefixProof()
    {
        var options = Options("dispatcher-proof");
        var hooks = new BlockingBoundaryHooks(WalCommitBoundary.BeforeMarkerStage);
        await using var store = NewStore(options, hooks);
        var dispatcher = new EventDispatcher(store);
        var projection = 0;

        var seq = dispatcher.Dispatch(NewOrder(0), () => projection = 1);
        Assert.Equal(1, seq);
        Assert.Equal(0, store.LastCommittedSeq);
        Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));

        long snapshotSeq = -1;
        var projectedAtCapture = 0;
        dispatcher.WithSnapshotLock(capturedSeq =>
        {
            snapshotSeq = capturedSeq;
            projectedAtCapture = projection;
        });

        Assert.Equal(seq, snapshotSeq);
        Assert.Equal(1, projectedAtCapture);

        var fence = dispatcher.FlushThroughAsync(seq).AsTask();
        hooks.Release.Set();
        await fence;
        Assert.Equal(seq, store.LastCommittedSeq);
        Assert.Equal(new ulong[] { 1 }, await ReplayIds(store));
    }

    [Fact]
    public async Task AppendAndTerminalFaultPublication_AreLinearizableAcrossRestart()
    {
        var options = Options("append-fault-race");
        var hooks = new BlockingBoundaryHooks(
            WalCommitBoundary.MarkerDirectoryFsynced,
            targetSeq: 1,
            throwOnRelease: true);
        var liveProjection = new List<ulong>();

        await using (var store = NewStore(options, hooks))
        {
            var dispatcher = new EventDispatcher(store);
            var first = dispatcher.Dispatch(
                NewOrder(0),
                () => liveProjection.Add(1));
            Assert.Equal(1, first);
            Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));

            // The second enqueue wins the same sequence/fault lock before the
            // writer is released to publish its terminal fault. It must return
            // success exactly once rather than enqueue and then throw.
            var second = dispatcher.Dispatch(
                NewOrder(1),
                () => liveProjection.Add(2));
            Assert.Equal(2, second);
            Assert.Equal(new ulong[] { 1, 2 }, liveProjection);

            hooks.Release.Set();
            await Assert.ThrowsAsync<WalFaultedException>(
                () => dispatcher.FlushThroughAsync(second).AsTask());
            Assert.False(store.IsHealthy);
            Assert.Equal(2, store.LastAdmittedSeq);
            Assert.Equal(1, store.LastAppendedSeq);
            Assert.Equal(0, store.LastCommittedSeq);

            var projectionBeforeRejectedAppend = liveProjection.Count;
            Assert.Throws<WalFaultedException>(() => dispatcher.Dispatch(
                NewOrder(2),
                () => liveProjection.Add(3)));
            Assert.Equal(projectionBeforeRejectedAppend, liveProjection.Count);
            Assert.Equal(2, store.LastAdmittedSeq);
        }

        // The first marker rename survived the injected post-directory-fsync
        // fault. The concurrently admitted second record never reached append
        // and therefore is absent after restart.
        await using var reopened = NewStore(options);
        Assert.Equal(1, reopened.LastCommittedSeq);
        Assert.Equal(new ulong[] { 1 }, await ReplayIds(reopened));
    }

    [Fact]
    public async Task AppendAndDispose_AreLinearizableAcrossRestart()
    {
        var options = Options("append-dispose-race");
        var hooks = new BlockingBoundaryHooks(WalCommitBoundary.BeforeMarkerStage);
        var store = NewStore(options, hooks);
        var dispatcher = new EventDispatcher(store);
        var projection = 0;

        dispatcher.Dispatch(NewOrder(0), () => projection++);
        Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));

        var dispose = store.DisposeAsync().AsTask();
        Assert.Throws<ObjectDisposedException>(() => dispatcher.Dispatch(
            NewOrder(1),
            () => projection++));
        Assert.Equal(1, projection);
        Assert.Equal(1, store.LastAdmittedSeq);

        hooks.Release.Set();
        await dispose;

        await using var reopened = NewStore(options);
        Assert.Equal(1, reopened.LastCommittedSeq);
        Assert.Equal(new ulong[] { 1 }, await ReplayIds(reopened));
    }

    [Fact]
    public async Task CancelledFence_DoesNotCancelCommitOrClaimDurability()
    {
        var options = Options("cancelled");
        var hooks = new BlockingBoundaryHooks(WalCommitBoundary.BeforeMarkerStage);
        await using var store = NewStore(options, hooks);
        var seq = store.Append(NewOrder(0));
        Assert.True(hooks.Entered.Wait(TimeSpan.FromSeconds(5)));

        using var cancellation = new CancellationTokenSource();
        var cancelledFence = store.FlushThroughAsync(seq, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledFence);
        Assert.Equal(0, store.LastCommittedSeq);

        hooks.Release.Set();
        await store.FlushThroughAsync(seq);
        Assert.Equal(seq, store.LastCommittedSeq);
    }

    [Fact]
    public async Task MarkerFault_IsStickyAndFailsEveryOutstandingFence()
    {
        var options = Options("sticky-marker");
        var hooks = new ThrowAtBoundaryHooks(WalCommitBoundary.BeforeMarkerStage);
        await using var store = NewStore(options, hooks);
        store.Append(NewOrder(0));
        store.Append(NewOrder(1));
        var low = store.FlushThroughAsync(1).AsTask();
        var high = store.FlushThroughAsync(2).AsTask();

        await Assert.ThrowsAsync<WalFaultedException>(() => low);
        await Assert.ThrowsAsync<WalFaultedException>(() => high);
        Assert.False(store.IsHealthy);
        Assert.NotNull(store.TerminalFault);
        Assert.Throws<WalFaultedException>(() => store.Append(NewOrder(2)));
        await Assert.ThrowsAsync<WalFaultedException>(
            () => store.FlushThroughAsync(1).AsTask());
    }

    [Fact]
    public async Task RequiredDirectoryFsyncFault_IsSticky()
    {
        var options = Options("directory-fsync");
        options.FsyncOnFlush = true;
        var durability = new ArmableDirectoryDurability();
        await using var store = new FileEventStore(
            options,
            NullLogger<FileEventStore>.Instance,
            durability,
            NoOpWalCommitBoundaryHooks.Instance);
        durability.Arm();

        var seq = store.Append(NewOrder(0));
        await Assert.ThrowsAsync<WalFaultedException>(
            () => store.FlushThroughAsync(seq).AsTask());
        Assert.False(store.IsHealthy);
        Assert.IsType<IOException>(store.TerminalFault);
    }

    [Fact]
    public async Task LegacyWal_RequiresExplicitControlledMigration()
    {
        var options = Options("legacy");
        var day = Path.Combine(WalRoot(options), "2026-01-01");
        Directory.CreateDirectory(day);
        var log = Path.Combine(day, "000.log");
        await using (var writer = new SegmentWriter(
                         log,
                         Path.Combine(day, "000.idx"),
                         64,
                         4096,
                         fsyncOnFlush: false))
        {
            writer.Append(1, Payload(NewOrder(0)), 0);
            writer.Flush();
        }

        Assert.Throws<WalLegacyMigrationRequiredException>(() => NewStore(options));

        options.LegacyWalStartupMode = LegacyWalStartupMode.ControlledCleanShutdown;
        Guid generation;
        await using (var migrated = NewStore(options))
        {
            generation = migrated.WalGeneration;
            Assert.NotEqual(Guid.Empty, generation);
            Assert.Equal(1, migrated.LastCommittedSeq);
            Assert.Equal(new ulong[] { 1 }, await ReplayIds(migrated));
        }
        await using var reopened = NewStore(options);
        Assert.Equal(generation, reopened.WalGeneration);
        Assert.Equal(1, reopened.LastCommittedSeq);
    }

    [Theory]
    [InlineData(WalCommitBoundary.MigrationMetadataStaged)]
    [InlineData(WalCommitBoundary.MigrationMetadataStagedAndFsynced)]
    [InlineData(WalCommitBoundary.BeforeMarkerStage)]
    [InlineData(WalCommitBoundary.MarkerStagedAndFsynced)]
    [InlineData(WalCommitBoundary.MarkerPublished)]
    [InlineData(WalCommitBoundary.MarkerDirectoryFsynced)]
    [InlineData(WalCommitBoundary.MigrationMetadataPublished)]
    [InlineData(WalCommitBoundary.MigrationMetadataDirectoryFsynced)]
    internal async Task ControlledLegacyMigration_CrashAtEveryPublicationBoundary_IsRestartSafe(
        WalCommitBoundary boundary)
    {
        var options = Options("legacy-crash-" + boundary);
        options.LegacyWalStartupMode = LegacyWalStartupMode.ControlledCleanShutdown;
        var day = Path.Combine(WalRoot(options), "2026-01-01");
        Directory.CreateDirectory(day);
        await WriteLegacySegment(options, day, ordinal: 0, seq: 1, NewOrder(0));
        await WriteLegacySegment(options, day, ordinal: 1, seq: 2, NewOrder(1));

        Assert.Throws<IOException>(() => NewStore(
            options,
            new ThrowAtBoundaryHooks(boundary)));

        var markerExists = File.Exists(
            Path.Combine(WalRoot(options), FileEventStore.MarkerFileName));
        if (!markerExists)
        {
            options.LegacyWalStartupMode =
                LegacyWalStartupMode.RejectUnknownShutdown;
            Assert.Throws<WalLegacyMigrationRequiredException>(
                () => NewStore(options));
            options.LegacyWalStartupMode =
                LegacyWalStartupMode.ControlledCleanShutdown;
        }
        else
        {
            // Once the marker is published, restart must complete any
            // partially-promoted metadata without relying on legacy mode.
            options.LegacyWalStartupMode =
                LegacyWalStartupMode.RejectUnknownShutdown;
        }

        await using var recovered = NewStore(options);
        Assert.Equal(2, recovered.LastCommittedSeq);
        Assert.Equal(new ulong[] { 1, 2 }, await ReplayIds(recovered));
        Assert.All(
            Directory.EnumerateFiles(
                WalRoot(options),
                "*.log.firstseq",
                SearchOption.AllDirectories),
            path => Assert.Equal(
                SegmentMetadata.EncodedLength,
                File.ReadAllBytes(path).Length));
        Assert.Empty(Directory.EnumerateFiles(
            WalRoot(options),
            "*.migrating",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptOrMissingMarker_OnMarkerFormatWal_FailsClosed()
    {
        var options = Options("bad-marker");
        await using (var store = NewStore(options))
        {
            store.Append(NewOrder(0));
            await store.FlushAsync();
        }

        var marker = Path.Combine(WalRoot(options), "commit.marker");
        var bytes = File.ReadAllBytes(marker);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(marker, bytes);
        Assert.Throws<WalRecoveryException>(() => NewStore(options));

        File.Delete(marker);
        Assert.Throws<WalLegacyMigrationRequiredException>(() => NewStore(options));
    }

    [Fact]
    public async Task RestartAfterSegmentRotation_PreservesGenerationAndSequence()
    {
        var options = Options("rotation");
        options.SegmentMaxBytes = 1;
        options.GroupCommitMaxRecords = 1;
        Guid generation;
        await using (var store = NewStore(options))
        {
            for (var i = 0; i < 5; i++)
                store.Append(NewOrder(i));
            await store.FlushAsync();
            generation = store.WalGeneration;
            Assert.Equal(5, store.LastCommittedSeq);
        }

        await using (var reopened = NewStore(options))
        {
            Assert.Equal(generation, reopened.WalGeneration);
            Assert.Equal(5, reopened.LastCommittedSeq);
            var seq = reopened.Append(NewOrder(5));
            await reopened.FlushThroughAsync(seq);
            Assert.Equal(6, seq);
        }

        await using var final = NewStore(options);
        Assert.Equal(Enumerable.Range(1, 6).Select(static i => (ulong)i), await ReplayIds(final));
    }

    private PersistenceOptions Options(string name) => new()
    {
        DataDirectory = Path.Combine(_root, name),
        FirmId = "test",
        ChannelCapacity = 64,
        GroupCommitMaxRecords = 16,
        GroupCommitWindow = TimeSpan.FromMilliseconds(5),
        SegmentMaxBytes = 4096,
        IndexEveryNRecords = 2,
        IndexEveryNBytes = 256,
        FsyncOnFlush = false,
        LegacyWalStartupMode = LegacyWalStartupMode.RejectUnknownShutdown,
    };

    private static FileEventStore NewStore(
        PersistenceOptions options,
        IWalCommitBoundaryHooks? hooks = null) =>
        new(
            options,
            NullLogger<FileEventStore>.Instance,
            ReconciliationDirectoryDurability.Instance,
            hooks ?? NoOpWalCommitBoundaryHooks.Instance);

    private static string WalRoot(PersistenceOptions options) =>
        Path.Combine(options.DataDirectory, options.FirmId, "wal");

    private static byte[] Payload(WalEvent evt) =>
        JsonSerializer.SerializeToUtf8Bytes(
            evt, WalEventJsonContext.Default.WalEvent);

    private static async Task<ulong[]> ReplayIds(FileEventStore store)
    {
        var ids = new List<ulong>();
        await foreach (var (_, evt) in store.ReadFromAsync(0))
            ids.Add(Assert.IsType<OrderSubmittedEvent>(evt).ClOrdId);
        return ids.ToArray();
    }

    private static async Task WriteLegacySegment(
        PersistenceOptions options,
        string dayDirectory,
        int ordinal,
        long seq,
        WalEvent evt)
    {
        var log = Path.Combine(dayDirectory, $"{ordinal:D3}.log");
        await using var writer = new SegmentWriter(
            log,
            Path.Combine(dayDirectory, $"{ordinal:D3}.idx"),
            options.IndexEveryNRecords,
            options.IndexEveryNBytes,
            fsyncOnFlush: false);
        writer.Append(seq, Payload(evt), evt.TimestampUtc.ToUnixTimeMilliseconds());
        writer.Flush();
    }

    private static OrderSubmittedEvent NewOrder(int i) => new()
    {
        ClOrdId = (ulong)(i + 1),
        EndClientId = "alice",
        FirmId = "TEST",
        Symbol = "PETR4",
        SecurityId = 4321,
        Side = "Buy",
        Type = "Limit",
        Quantity = 100,
        Price = 30m,
        TimestampUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private sealed class ThrowAtBoundaryHooks(
        WalCommitBoundary target,
        long? targetSeq = null)
        : IWalCommitBoundaryHooks
    {
        private int _thrown;

        public void OnBoundary(WalCommitBoundary boundary, long seq)
        {
            if (boundary == target
                && (targetSeq is null || targetSeq == seq)
                && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new IOException($"Injected WAL boundary fault at {boundary} for seq {seq}.");
        }
    }

    private sealed class BlockingBoundaryHooks(
        WalCommitBoundary target,
        long? targetSeq = null,
        bool throwOnRelease = false)
        : IWalCommitBoundaryHooks
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public void OnBoundary(WalCommitBoundary boundary, long seq)
        {
            if (boundary != target || (targetSeq is not null && targetSeq != seq))
                return;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release injected WAL boundary.");
            if (throwOnRelease)
                throw new IOException(
                    $"Injected WAL boundary fault at {boundary} for seq {seq}.");
        }
    }

    private sealed class ArmableDirectoryDurability : IReconciliationDirectoryDurability
    {
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Flush(string directoryPath)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                throw new IOException("Injected directory fsync failure.");
            ReconciliationDirectoryDurability.Instance.Flush(directoryPath);
        }
    }
}
