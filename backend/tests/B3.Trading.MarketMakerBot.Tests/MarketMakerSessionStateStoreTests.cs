using B3.EntryPoint.Client.State;

namespace B3.Trading.MarketMakerBot.Tests;

public sealed class MarketMakerSessionStateStoreTests
{
    [Fact]
    public async Task RetiredOrderCannotReappearInLaterStaleSnapshotCompaction()
    {
        var directory = CreateStateDirectory();
        try
        {
            var store = new MarketMakerSessionStateStore(directory);
            var staleSnapshot = Snapshot((100, 1), (200, 2));
            await store.SaveAsync(staleSnapshot);
            await store.RetireOrderAsync(100);

            await store.SaveAsync(staleSnapshot);
            var replayed = await store.ReplayAsync();

            Assert.NotNull(replayed);
            Assert.Equal(200ul, replayed.LastInboundSeqNum);
            Assert.DoesNotContain("100", replayed.OutstandingOrders.Keys);
            Assert.Equal(2ul, replayed.OutstandingOrders["200"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetiredPendingNewsStayRetiredAcrossProcessRestart()
    {
        var directory = CreateStateDirectory();
        try
        {
            var store = new MarketMakerSessionStateStore(directory);
            await store.SaveAsync(Snapshot(
                (101, 1), (102, 1), (103, 2), (104, 2), (105, 3), (106, 3)));
            for (ulong clOrdId = 101; clOrdId <= 106; clOrdId++)
                await store.RetireOrderAsync(clOrdId);

            var restartedStore = new MarketMakerSessionStateStore(directory);
            var replayed = await restartedStore.ReplayAsync();

            Assert.NotNull(replayed);
            Assert.Equal(42u, replayed.SessionVerId);
            Assert.Equal(3060ul, replayed.LastOutboundSeqNum);
            Assert.Equal(200ul, replayed.LastInboundSeqNum);
            Assert.Empty(replayed.OutstandingOrders);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetiredNewStaysRetiredWhenOutboundDeltaIsPersistedLater()
    {
        var directory = CreateStateDirectory();
        try
        {
            var store = new MarketMakerSessionStateStore(directory);
            await store.SaveAsync(Snapshot());
            await store.RetireOrderAsync(100);
            await store.AppendDeltaAsync(new OutboundDelta(3061, "100", 1));

            var restartedStore = new MarketMakerSessionStateStore(directory);
            var replayed = await restartedStore.ReplayAsync();

            Assert.NotNull(replayed);
            Assert.Equal(3061ul, replayed.LastOutboundSeqNum);
            Assert.Equal(200ul, replayed.LastInboundSeqNum);
            Assert.DoesNotContain("100", replayed.OutstandingOrders.Keys);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReconciliationRequirementPersistsAcrossProcessRestart()
    {
        var directory = CreateStateDirectory();
        try
        {
            var store = new MarketMakerSessionStateStore(directory);
            await store.RequireReconciliationAsync("unknown fill tradeId=55");

            var restartedStore = new MarketMakerSessionStateStore(directory);
            var requirement = await restartedStore.GetReconciliationRequirementAsync();

            Assert.NotNull(requirement);
            Assert.Equal("unknown fill tradeId=55", requirement.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ContiguousInboundFenceIsScopedToSessionVersion()
    {
        var directory = CreateStateDirectory();
        try
        {
            var store = new MarketMakerSessionStateStore(directory);
            await store.SaveAsync(Snapshot());
            await store.RecordContiguousInboundAsync(
                sessionId: 10102,
                sessionVerId: 42,
                contiguousSeqNum: 205);

            var resumed = await new MarketMakerSessionStateStore(directory).ReplayAsync();
            Assert.NotNull(resumed);
            Assert.Equal(205ul, resumed.LastInboundSeqNum);

            await store.SaveAsync(new SessionSnapshot
            {
                SessionId = 10102,
                SessionVerId = 43,
                CapturedAt = DateTimeOffset.UtcNow,
            });
            var advanced = await new MarketMakerSessionStateStore(directory).ReplayAsync();
            Assert.NotNull(advanced);
            Assert.Equal(43u, advanced.SessionVerId);
            Assert.Equal(0ul, advanced.LastInboundSeqNum);

            await store.RecordContiguousInboundAsync(
                sessionId: 10102,
                sessionVerId: 43,
                contiguousSeqNum: 10);
            await store.SaveAsync(new SessionSnapshot
            {
                SessionId = 10103,
                SessionVerId = 43,
                CapturedAt = DateTimeOffset.UtcNow,
            });
            var changedSession = await new MarketMakerSessionStateStore(directory).ReplayAsync();
            Assert.NotNull(changedSession);
            Assert.Equal(10103u, changedSession.SessionId);
            Assert.Equal(0ul, changedSession.LastInboundSeqNum);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SessionSnapshot Snapshot(params (ulong ClOrdId, ulong SecurityId)[] orders) =>
        new()
        {
            SessionId = 10102,
            SessionVerId = 42,
            LastOutboundSeqNum = 3060,
            LastInboundSeqNum = 200,
            CapturedAt = DateTimeOffset.UtcNow,
            OutstandingOrders = orders.ToDictionary(
                order => order.ClOrdId.ToString(),
                order => order.SecurityId,
                StringComparer.Ordinal),
        };

    private static string CreateStateDirectory()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "session-state-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
