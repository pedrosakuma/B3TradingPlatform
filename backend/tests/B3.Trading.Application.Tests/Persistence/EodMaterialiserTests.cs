using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

public class EodMaterialiserTests : IDisposable
{
    private readonly string _root;

    public EodMaterialiserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-eod-" + Guid.NewGuid().ToString("N"));
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
            DataDirectory = _root, FirmId = "test", FsyncOnFlush = false,
            GroupCommitMaxRecords = 1, GroupCommitWindow = TimeSpan.FromMilliseconds(2),
        };
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayTs = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        await using (var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance))
        {
            store.Append(new OrderSubmittedEvent
            {
                ClOrdId = 1UL, EndClientId = "alice", FirmId = "TEST", Symbol = "PETR4", SecurityId = 4321UL,
                Side = "Buy", Type = "Limit", Quantity = 100, Price = 30m,
                TimestampUtc = dayTs,
            });
            store.Append(new ExecutionReportReceivedEvent
            {
                ClOrdId = 1UL, ExecKind = "Fill", LeavesQuantity = 0, CumulativeQuantity = 100,
                LastQuantity = 100, LastPrice = 30m, Synthetic = false,
                TimestampUtc = dayTs,
            });
            store.Append(new ExecutionReportReceivedEvent
            {
                ClOrdId = 2UL, ExecKind = "Rejected", LeavesQuantity = 0, CumulativeQuantity = 0,
                LastQuantity = 0, LastPrice = 0m, Synthetic = true, RejectReason = "risk",
                TimestampUtc = dayTs,
            });
            store.Append(new KillSwitchToggledEvent
            {
                Scope = "firm", Target = "TEST", Killed = true, TimestampUtc = dayTs,
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
}
