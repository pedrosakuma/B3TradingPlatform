using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

public class EodMaterialiserTests : IDisposable
{
    private readonly string _root;

    public EodMaterialiserTests()
    {
        _root = Path.Combine(
            AppContext.BaseDirectory,
            "eod-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Materialise_AggregatesCountsByEventKindAndWritesFile()
    {
        var opts = new PersistenceOptions
        {
            DataDirectory = _root,
            FirmId = "test",
            FsyncOnFlush = false,
            GroupCommitMaxRecords = 1,
            GroupCommitWindow = TimeSpan.FromMilliseconds(2),
        };
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayTs = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        await using (var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance))
        {
            store.Append(new OrderSubmittedEvent
            {
                ClOrdId = 1UL,
                EndClientId = "alice",
                FirmId = "TEST",
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = 100,
                Price = 30m,
                TimestampUtc = dayTs,
            });
            store.Append(new ExecutionReportReceivedEvent
            {
                ClOrdId = 1UL,
                ExecKind = "Fill",
                LeavesQuantity = 0,
                CumulativeQuantity = 100,
                LastQuantity = 100,
                LastPrice = 30m,
                Synthetic = false,
                TimestampUtc = dayTs,
            });
            store.Append(new ExecutionReportReceivedEvent
            {
                ClOrdId = 2UL,
                ExecKind = "Rejected",
                LeavesQuantity = 0,
                CumulativeQuantity = 0,
                LastQuantity = 0,
                LastPrice = 0m,
                Synthetic = true,
                RejectReason = "risk",
                TimestampUtc = dayTs,
            });
            store.Append(new KillSwitchToggledEvent
            {
                Scope = "firm",
                Target = "TEST",
                Killed = true,
                TimestampUtc = dayTs,
            });
            await store.FlushAsync();
        }

        var report = new EodMaterialiser(opts).Materialise(today);
        Assert.Equal(4, report.RecordCount);
        Assert.Equal(1, report.OrderSubmittedCount);
        Assert.Equal(2, report.ExecutionReportCount);
        Assert.Equal(1, report.FilledCount);
        Assert.Equal(1, report.RejectedCount);
        Assert.Equal(1, report.KillSwitchToggleCount);
        Assert.False(string.IsNullOrEmpty(report.Sha256));
        Assert.True(File.Exists(report.Path));
    }

    [Fact]
    public async Task Materialise_CapsCommittedTailAndExcludesCompleteSurvivor()
    {
        var opts = Options();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayTs = new DateTimeOffset(
            today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await using (var store = new FileEventStore(
                         opts,
                         NullLogger<FileEventStore>.Instance,
                         ReconciliationDirectoryDurability.Instance,
                         new ThrowAtBoundaryHooks(
                             WalCommitBoundary.LogFsynced,
                             targetSeq: 2)))
        {
            var first = store.Append(NewOrder(1, dayTs));
            await store.FlushThroughAsync(first);
            var survivor = store.Append(NewOrder(2, dayTs));
            await Assert.ThrowsAsync<WalFaultedException>(
                () => store.FlushThroughAsync(survivor).AsTask());
        }

        var report = new EodMaterialiser(opts).Materialise(today);
        Assert.Equal(1, report.RecordCount);
        Assert.Equal(1, report.OrderSubmittedCount);
    }

    [Fact]
    public async Task Materialise_FailsClosedOnCommittedMarkerMetadataMismatch()
    {
        var opts = Options();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayTs = new DateTimeOffset(
            today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await using (var store = new FileEventStore(
                         opts, NullLogger<FileEventStore>.Instance))
        {
            store.Append(NewOrder(1, dayTs));
            await store.FlushAsync();
        }

        var metadata = Directory.EnumerateFiles(
            Path.Combine(_root, "test", "wal"),
            "*.log.firstseq",
            SearchOption.AllDirectories).Single();
        File.WriteAllBytes(
            metadata,
            SegmentMetadata.Encode(Guid.NewGuid(), firstSeq: 1));

        Assert.Throws<WalRecoveryException>(
            () => new EodMaterialiser(opts).Materialise(today));
    }

    [Fact]
    public async Task Materialise_LegacyWalRequiresControlledMigrationAndFullValidation()
    {
        var opts = Options();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayTs = new DateTimeOffset(
            today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayDir = Path.Combine(
            _root,
            "test",
            "wal",
            today.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayDir);
        var log = Path.Combine(dayDir, "000.log");
        await using (var writer = new SegmentWriter(
                         log,
                         Path.Combine(dayDir, "000.idx"),
                         opts.IndexEveryNRecords,
                         opts.IndexEveryNBytes,
                         fsyncOnFlush: false))
        {
            writer.Append(
                1,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<WalEvent>(
                    NewOrder(1, dayTs),
                    WalEventJsonContext.Default.WalEvent),
                dayTs.ToUnixTimeMilliseconds());
            writer.Flush();
        }

        Assert.Throws<WalLegacyMigrationRequiredException>(
            () => new EodMaterialiser(opts).Materialise(today));

        opts.LegacyWalStartupMode =
            LegacyWalStartupMode.ControlledCleanShutdown;
        Assert.Equal(
            1,
            new EodMaterialiser(opts).Materialise(today).RecordCount);

        await File.AppendAllBytesAsync(log, [0x01, 0x02, 0x03]);
        Assert.Throws<WalRecoveryException>(
            () => new EodMaterialiser(opts).Materialise(today));
    }

    private PersistenceOptions Options() => new()
    {
        DataDirectory = _root,
        FirmId = "test",
        FsyncOnFlush = false,
        GroupCommitMaxRecords = 1,
        GroupCommitWindow = TimeSpan.FromMilliseconds(2),
        IndexEveryNRecords = 2,
        IndexEveryNBytes = 256,
    };

    private static OrderSubmittedEvent NewOrder(
        ulong clOrdId,
        DateTimeOffset timestamp) =>
        new()
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
            TimestampUtc = timestamp,
        };

    private sealed class ThrowAtBoundaryHooks(
        WalCommitBoundary target,
        long targetSeq)
        : IWalCommitBoundaryHooks
    {
        public void OnBoundary(WalCommitBoundary boundary, long seq)
        {
            if (boundary == target && seq == targetSeq)
                throw new IOException(
                    $"Injected EOD survivor fault at {boundary} for seq {seq}.");
        }
    }
}
