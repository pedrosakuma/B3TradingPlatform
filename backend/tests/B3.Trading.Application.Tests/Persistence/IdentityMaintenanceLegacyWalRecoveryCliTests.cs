using System.Text.Json;
using B3.Trading.Application.Identity;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Outbound;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Persistence;

public sealed class IdentityMaintenanceLegacyWalRecoveryCliTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(),
        "TestResults",
        "LegacyWalRecovery",
        Guid.NewGuid().ToString("N"));

    public IdentityMaintenanceLegacyWalRecoveryCliTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RecoverLegacyWal_CoveredLegacySnapshot_PublishesMarkerAndAuditRecord()
    {
        var options = Options(nameof(RecoverLegacyWal_CoveredLegacySnapshot_PublishesMarkerAndAuditRecord));
        var day = Path.Combine(WalRoot(options), "2026-01-01");
        Directory.CreateDirectory(day);
        await WriteLegacySegment(options, day, ordinal: 0, seq: 1, NewOrder(0));
        new SnapshotStore(options.DataDirectory, options.FirmId).Write(new PlatformSnapshot
        {
            Seq = 1,
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero),
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await IdentityMaintenanceCli.RunAsync(
            [
                "recover-legacy-wal",
                "--data-directory", options.DataDirectory,
                "--firm-id", options.FirmId,
                "--operator", "ops-user",
                "--change-ticket", "INC-670",
                "--reason", "incident-670 controlled marker publication",
                "--i-understand-this-promotes-a-legacy-wal-without-proving-the-tail-was-durable",
            ],
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("recover-legacy-wal", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("recovered", json.RootElement.GetProperty("Status").GetString());
        Assert.Equal("legacy_wal_marker_published", json.RootElement.GetProperty("ReasonCode").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("LastDurableSeq").GetInt64());

        var auditLogPath = json.RootElement.GetProperty("AuditLogPath").GetString();
        Assert.NotNull(auditLogPath);
        Assert.True(File.Exists(auditLogPath));
        using var audit = JsonDocument.Parse(File.ReadAllText(auditLogPath!));
        Assert.Equal("ops-user", audit.RootElement.GetProperty("Operator").GetString());
        Assert.Equal("INC-670", audit.RootElement.GetProperty("ChangeTicket").GetString());

        await using var reopened = NewStore(options);
        Assert.Equal(1, reopened.LastCommittedSeq);
        var replayed = new List<ulong>();
        await foreach (var (_, evt) in reopened.ReadFromAsync(0))
            replayed.Add(Assert.IsType<OrderSubmittedEvent>(evt).ClOrdId);
        Assert.Equal(new ulong[] { 1 }, replayed);
    }

    [Fact]
    public async Task RecoverLegacyWal_LatestSnapshotAheadOfRecoverablePrefix_RefusesWithoutWritingMarker()
    {
        var options = Options(nameof(RecoverLegacyWal_LatestSnapshotAheadOfRecoverablePrefix_RefusesWithoutWritingMarker));
        var day = Path.Combine(WalRoot(options), "2026-01-01");
        Directory.CreateDirectory(day);
        await WriteLegacySegment(options, day, ordinal: 0, seq: 1, NewOrder(0));
        new SnapshotStore(options.DataDirectory, options.FirmId).Write(new PlatformSnapshot
        {
            Seq = 2,
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero),
        });

        var stderr = new StringWriter();
        var exitCode = await IdentityMaintenanceCli.RunAsync(
            [
                "recover-legacy-wal",
                "--data-directory", options.DataDirectory,
                "--firm-id", options.FirmId,
                "--operator", "ops-user",
                "--change-ticket", "INC-670",
                "--reason", "incident-670 controlled marker publication",
                "--i-understand-this-promotes-a-legacy-wal-without-proving-the-tail-was-durable",
            ],
            new StringWriter(),
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("does not exactly match the recoverable legacy WAL prefix", stderr.ToString());
        Assert.False(File.Exists(Path.Combine(WalRoot(options), FileEventStore.MarkerFileName)));
    }

    [Fact]
    public async Task RecoverLegacyWal_LatestSnapshotBehindRecoverablePrefix_RefusesWithoutWritingMarker()
    {
        var options = Options(nameof(RecoverLegacyWal_LatestSnapshotBehindRecoverablePrefix_RefusesWithoutWritingMarker));
        var day = Path.Combine(WalRoot(options), "2026-01-01");
        Directory.CreateDirectory(day);
        await WriteLegacySegment(options, day, ordinal: 0, seq: 1, NewOrder(0));
        await WriteLegacySegment(options, day, ordinal: 1, seq: 2, NewOrder(1));
        new SnapshotStore(options.DataDirectory, options.FirmId).Write(new PlatformSnapshot
        {
            Seq = 1,
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero),
        });

        var stderr = new StringWriter();
        var exitCode = await IdentityMaintenanceCli.RunAsync(
            [
                "recover-legacy-wal",
                "--data-directory", options.DataDirectory,
                "--firm-id", options.FirmId,
                "--operator", "ops-user",
                "--change-ticket", "INC-670",
                "--reason", "incident-670 controlled marker publication",
                "--i-understand-this-promotes-a-legacy-wal-without-proving-the-tail-was-durable",
            ],
            new StringWriter(),
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("does not exactly match the recoverable legacy WAL prefix", stderr.ToString());
        Assert.False(File.Exists(Path.Combine(WalRoot(options), FileEventStore.MarkerFileName)));
    }

    [Fact]
    public async Task RecoverLegacyWal_WhenMarkerAlreadyExists_IsANoOp()
    {
        var options = Options(nameof(RecoverLegacyWal_WhenMarkerAlreadyExists_IsANoOp));
        await using (var store = NewStore(options))
        {
            store.Append(NewOrder(0));
            await store.FlushAsync();
        }

        var stdout = new StringWriter();
        var exitCode = await IdentityMaintenanceCli.RunAsync(
            [
                "recover-legacy-wal",
                "--data-directory", options.DataDirectory,
                "--firm-id", options.FirmId,
                "--operator", "ops-user",
                "--change-ticket", "INC-670",
                "--reason", "incident-670 controlled marker publication",
                "--i-understand-this-promotes-a-legacy-wal-without-proving-the-tail-was-durable",
            ],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("no_action_needed", json.RootElement.GetProperty("Status").GetString());
        Assert.Equal("commit_marker_present", json.RootElement.GetProperty("ReasonCode").GetString());
    }

    [Fact]
    public async Task RecoverLegacyWal_WithoutExplicitConfirmation_FailsClosed()
    {
        var options = Options(nameof(RecoverLegacyWal_WithoutExplicitConfirmation_FailsClosed));
        var day = Path.Combine(WalRoot(options), "2026-01-01");
        Directory.CreateDirectory(day);
        await WriteLegacySegment(options, day, ordinal: 0, seq: 1, NewOrder(0));

        var stderr = new StringWriter();
        var exitCode = await IdentityMaintenanceCli.RunAsync(
            [
                "recover-legacy-wal",
                "--data-directory", options.DataDirectory,
                "--firm-id", options.FirmId,
                "--operator", "ops-user",
                "--change-ticket", "INC-670",
                "--reason", "incident-670 controlled marker publication",
            ],
            new StringWriter(),
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Explicit confirmation is required", stderr.ToString());
        Assert.False(File.Exists(Path.Combine(WalRoot(options), FileEventStore.MarkerFileName)));
    }

    [Fact]
    public async Task RecoverLegacyWal_WhenHostFenceIsHeldForDifferentExchangeFirm_Refuses()
    {
        var options = Options(nameof(RecoverLegacyWal_WhenHostFenceIsHeldForDifferentExchangeFirm_Refuses));
        options.FirmId = "deployment";
        var day = Path.Combine(WalRoot(options), "2026-01-01");
        Directory.CreateDirectory(day);
        await WriteLegacySegment(options, day, ordinal: 0, seq: 1, NewOrder(0));

        using var fence = new ActiveHostFence(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new ExchangeOptions
            {
                Firms =
                [
                    new FirmConfig { FirmId = "FIRM01" },
                ],
            }),
            OutboundProcessEpoch.CreateUninitialized(),
            new OutboundRecoveryState(new OutboundMutationLedger()),
            NullLogger<ActiveHostFence>.Instance);
        Assert.True(fence.TryAcquire());

        var stderr = new StringWriter();
        var exitCode = await IdentityMaintenanceCli.RunAsync(
            [
                "recover-legacy-wal",
                "--data-directory", options.DataDirectory,
                "--firm-id", options.FirmId,
                "--operator", "ops-user",
                "--change-ticket", "INC-670",
                "--reason", "incident-670 controlled marker publication",
                "--i-understand-this-promotes-a-legacy-wal-without-proving-the-tail-was-durable",
            ],
            new StringWriter(),
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("active-host fence", stderr.ToString());
        Assert.False(File.Exists(Path.Combine(WalRoot(options), FileEventStore.MarkerFileName)));
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

    private static FileEventStore NewStore(PersistenceOptions options) =>
        new(options, NullLogger<FileEventStore>.Instance);

    private static string WalRoot(PersistenceOptions options) =>
        Path.Combine(options.DataDirectory, options.FirmId, "wal");

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
        writer.Append(
            seq,
            JsonSerializer.SerializeToUtf8Bytes(evt, WalEventJsonContext.Default.WalEvent),
            evt.TimestampUtc.ToUnixTimeMilliseconds());
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
}
