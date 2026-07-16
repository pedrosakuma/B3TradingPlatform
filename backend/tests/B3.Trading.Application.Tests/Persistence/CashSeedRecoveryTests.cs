using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

public sealed class CashSeedRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "b3tp-cash-seed-recovery-" + Guid.NewGuid().ToString("N"));

    public CashSeedRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task WalOnlyRecovery_AppliesOpeningCashBeforeFillReplay()
    {
        var owner = new EndClientId("bob-firm02");
        var options = Options();

        await using (var store = new FileEventStore(options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            state.Cash.SeedIfAbsent("FIRM02", owner, 100_000m);
            DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, owner);
            DispatchFill(state.Dispatcher, state.Processor);
            Assert.Equal(90_761.48m, state.Cash.GetAvailable("FIRM02", owner));
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            var replayer = new EventReplayer(
                state.Book,
                state.Ownership,
                state.KillSwitch,
                state.SymbolHalts,
                state.SessionPhases,
                state.Processor,
                state.Algos,
                state.ClOrdIds,
                state.AlgoIds);
            var recovery = new PersistenceRecovery(
                store,
                state.Snapshotter,
                replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);

            await recovery.RunAsync(() =>
                state.Cash.SeedIfAbsent("FIRM02", owner, 100_000m));

            Assert.Equal(90_761.48m, state.Cash.GetAvailable("FIRM02", owner));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(75_000)]
    public async Task SnapshotBalance_RemainsAuthoritativeWhenOpeningCashIsConfigured(
        decimal restoredBalance)
    {
        var owner = new EndClientId("bob-firm02");
        var options = Options();

        await using (var store = new FileEventStore(options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            state.Cash.SeedIfAbsent("FIRM02", owner, restoredBalance);
            PlatformSnapshot? snapshot = null;
            state.Dispatcher.WithSnapshotLock(seq => snapshot = state.Snapshotter.Capture(seq));
            new SnapshotStore(_root, "test").Write(snapshot!);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            var replayer = new EventReplayer(
                state.Book,
                state.Ownership,
                state.KillSwitch,
                state.SymbolHalts,
                state.SessionPhases,
                state.Processor,
                state.Algos,
                state.ClOrdIds,
                state.AlgoIds);
            var recovery = new PersistenceRecovery(
                store,
                state.Snapshotter,
                replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);

            await recovery.RunAsync(() =>
                state.Cash.SeedIfAbsent("FIRM02", owner, 100_000m));

            Assert.Equal(restoredBalance, state.Cash.GetAvailable("FIRM02", owner));
        }
    }

    [Fact]
    public async Task WalOnlyRecovery_RebuildsRemainingMarginForPartiallyFilledBuy()
    {
        var owner = new EndClientId("bob-firm02");
        var options = Options();

        await using (var store = new FileEventStore(options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            state.Cash.SeedIfAbsent("FIRM02", owner, 100_000m);
            DispatchSubmit(state.Dispatcher, state.Book, state.Ownership, owner);
            DispatchExecution(
                state.Dispatcher,
                state.Processor,
                ExecKind.PartialFill,
                leaves: 70,
                cumulative: 30,
                last: 30);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            var replayer = new EventReplayer(
                state.Book,
                state.Ownership,
                state.KillSwitch,
                state.SymbolHalts,
                state.SessionPhases,
                state.Processor,
                state.Algos,
                state.ClOrdIds,
                state.AlgoIds);
            var recovery = new PersistenceRecovery(
                store,
                state.Snapshotter,
                replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance,
                orders: state.Book,
                marginProvider: state.Margin);

            await recovery.RunAsync(() =>
                state.Cash.SeedIfAbsent("FIRM02", owner, 100_000m));

            Assert.Equal(97_228.444m, state.Cash.GetAvailable("FIRM02", owner));
            Assert.Equal(6_466.964m, state.Margin.ReservedForTesting("FIRM02", owner.Value));
            Assert.Equal(90_761.48m, state.Margin.AvailableForTesting("FIRM02", owner.Value));
        }
    }

    [Fact]
    public async Task SnapshotRecovery_ActivatesConservativeThrottleFences()
    {
        var options = Options();
        await using (var store = new FileEventStore(
                         options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            PlatformSnapshot? snapshot = null;
            state.Dispatcher.WithSnapshotLock(seq =>
                snapshot = state.Snapshotter.Capture(seq));
            new SnapshotStore(_root, "test").Write(snapshot!);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(
                         options, NullLogger<FileEventStore>.Instance))
        {
            var state = BuildState(store);
            var replayer = new EventReplayer(
                state.Book,
                state.Ownership,
                state.KillSwitch,
                state.SymbolHalts,
                state.SessionPhases,
                state.Processor,
                state.Algos,
                state.ClOrdIds,
                state.AlgoIds);
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var risk = new RiskOptions
            {
                RollingNotional = new RollingNotionalOptions { WindowSeconds = 60 },
                OrderRate = new OrderRateOptions { WindowSeconds = 60 },
            };
            var monitor = new StaticOptionsMonitor<RiskOptions>(risk);
            var notional = new RollingNotionalAccountant(
                monitor, new MissingReferencePrice(), clock);
            var rate = new OrderRateAccountant(monitor, clock);
            var recovery = new PersistenceRecovery(
                store,
                state.Snapshotter,
                replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance,
                riskRecoveryFences: [notional, rate]);

            await recovery.RunAsync();

            Assert.True(notional.IsRecoveryFenced);
            Assert.True(rate.IsRecoveryFenced);
            clock.Advance(TimeSpan.FromSeconds(61));
            Assert.False(notional.IsRecoveryFenced);
            Assert.False(rate.IsRecoveryFenced);
        }
    }

    private PersistenceOptions Options() => new()
    {
        DataDirectory = _root,
        FirmId = "test",
        ChannelCapacity = 64,
        GroupCommitMaxRecords = 8,
        GroupCommitWindow = TimeSpan.FromMilliseconds(5),
        FsyncOnFlush = false,
    };

    private static State BuildState(IEventStore store)
    {
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var cash = new CashLedger();
        var killSwitch = new KillSwitchService();
        var symbolHalts = new SymbolHaltService();
        var sessionPhases = new SessionPhaseService();
        var algos = new AlgoBook();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algoIds = new AlgoIdRegistry();
        var riskOptions = new RiskOptions();
        riskOptions.Margin.Enabled = true;
        var margin = new ReserveOnSubmitMarginProvider(
            new StaticOptionsMonitor<RiskOptions>(riskOptions),
            NullLogger<ReserveOnSubmitMarginProvider>.Instance,
            cash);
        var processor = new ExecutionReportProcessor(
            ownership,
            book,
            positions,
            new NullSink(),
            margin,
            NullLogger<ExecutionReportProcessor>.Instance,
            cash: cash);
        var snapshotter = new StateSnapshotter(
            book,
            positions,
            killSwitch,
            symbolHalts,
            sessionPhases,
            clOrdIds,
            ownership,
            algos,
            algoIds,
            cash);
        return new State(
            book,
            ownership,
            cash,
            killSwitch,
            symbolHalts,
            sessionPhases,
            algos,
            clOrdIds,
            algoIds,
            margin,
            processor,
            snapshotter,
            new EventDispatcher(store));
    }

    private static void DispatchSubmit(
        EventDispatcher dispatcher,
        WorkingOrderBook book,
        OrderOwnershipMap ownership,
        EndClientId owner)
    {
        dispatcher.Dispatch(
            new OrderSubmittedEvent
            {
                ClOrdId = 1,
                EndClientId = owner.Value,
                FirmId = "FIRM02",
                Symbol = "PETR4",
                SecurityId = 1234,
                Side = OrderSide.Buy.ToString(),
                Type = OrderType.Limit.ToString(),
                Quantity = 100,
                Price = 92.3852m,
            },
            () =>
            {
                ownership.Register(1, owner);
                book.TryAdd(new Order(
                    1,
                    owner,
                    "PETR4",
                    1234,
                    OrderSide.Buy,
                    OrderType.Limit,
                    100,
                    92.3852m,
                    "FIRM02"));
            });
    }

    private static void DispatchFill(EventDispatcher dispatcher, ExecutionReportProcessor processor) =>
        DispatchExecution(
            dispatcher,
            processor,
            ExecKind.Fill,
            leaves: 0,
            cumulative: 100,
            last: 100);

    private static void DispatchExecution(
        EventDispatcher dispatcher,
        ExecutionReportProcessor processor,
        ExecKind kind,
        long leaves,
        long cumulative,
        long last)
    {
        dispatcher.Dispatch(
            new ExecutionReportReceivedEvent
            {
                ClOrdId = 1,
                ExecKind = kind.ToString(),
                LeavesQuantity = leaves,
                CumulativeQuantity = cumulative,
                LastQuantity = last,
                LastPrice = 92.3852m,
                Synthetic = false,
                FirmId = "FIRM02",
            },
            () => processor.Apply(
                1,
                kind,
                leaves: leaves,
                cumQty: cumulative,
                lastQty: last,
                lastPx: 92.3852m,
                rejectReason: null,
                origClOrdId: 0,
                envelopeFirmId: "FIRM02"));
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }

    private sealed class MissingReferencePrice : IReferencePrice
    {
        public bool TryGet(string symbol, out decimal price)
        {
            price = 0m;
            return false;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record State(
        WorkingOrderBook Book,
        OrderOwnershipMap Ownership,
        CashLedger Cash,
        KillSwitchService KillSwitch,
        SymbolHaltService SymbolHalts,
        SessionPhaseService SessionPhases,
        AlgoBook Algos,
        ClOrdIdPrefixRegistry ClOrdIds,
        AlgoIdRegistry AlgoIds,
        ReserveOnSubmitMarginProvider Margin,
        ExecutionReportProcessor Processor,
        StateSnapshotter Snapshotter,
        EventDispatcher Dispatcher);
}
