using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Sub-issue #171 (E). Behavioural coverage for the shared cancel
/// pipeline introduced by this slice. The two invariants that matter
/// most here:
///
/// <list type="number">
///   <item>The dispatcher <c>apply</c> callback runs synchronous
///   in-memory mutations only — the async gateway call must happen
///   AFTER <c>Dispatch</c> returns. A fake gateway that records call
///   ordering pins this contract.</item>
///   <item>When a <see cref="BotOrigin"/> is supplied, the bot
///   cancel-mapping registry is populated atomically with the WAL
///   record; otherwise it is left untouched (REST/WS unaffected).</item>
/// </list>
/// </summary>
public class OrderCancelServiceTests
{
    private static readonly Guid CredA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly EndClientId Owner = new("alice");

    private sealed class RecordingGateway : IExchangeGateway
    {
        public List<string> Calls { get; } = new();
        public TaskCompletionSource? CancelGate { get; set; }

        public Task SubmitAsync(Order order, CancellationToken cancellationToken)
        {
            Calls.Add("submit");
            return Task.CompletedTask;
        }

        public async Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
        {
            Calls.Add($"cancel:{newClOrdId}");
            if (CancelGate is { } g) await g.Task.ConfigureAwait(false);
        }

        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static Order Working(ulong clOrdId)
        => new(clOrdId, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m);

    [Fact]
    public async Task CancelAsync_HappyPath_AppliesInMemoryMutations_AndCallsGatewayOutsideDispatch()
    {
        // The apply callback must mutate ownership/clOrdId/registry
        // SYNCHRONOUSLY, and the gateway must be hit AFTER dispatch returns.
        // A blocking gate on the gateway proves the dispatcher lock is
        // released before the network I/O — otherwise nothing else could
        // proceed during a flaky cancel.
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var gateway = new RecordingGateway();
        var bots = new InMemoryUserBotOrderMappingRegistry();

        // Seed an existing order owned by Alice (mimic prior submit).
        const ulong original = 100UL;
        Assert.True(book.TryAdd(Working(original)));
        ownership.Register(original, Owner);
        bots.RegisterOrderInternal(original, CredA, externalClOrdId: 9UL);

        var sut = new OrderCancelService(clOrdIds, ownership, book, gateway, dispatcher,
            NullLogger<OrderCancelService>.Instance, bots);

        // Hold the gateway call so the test can observe ordering between
        // dispatcher-synchronous mutations and the OUTSIDE-the-lock I/O.
        gateway.CancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = sut.CancelAsync(Owner, original, CancellationToken.None,
            new BotOrigin(CredA, ExternalClOrdId: 77UL));

        for (int i = 0; i < 100 && gateway.Calls.Count == 0; i++)
            await Task.Yield();

        // Gateway has been called, blocked on the gate — meaning Dispatch
        // already returned with its in-memory mutations applied.
        Assert.Single(gateway.Calls);
        Assert.StartsWith("cancel:", gateway.Calls[0]);
        var cancelClOrdId = ulong.Parse(gateway.Calls[0]["cancel:".Length..]);

        // ownership cancel-link, watermark advance, and bot mapping all
        // visible without waiting for the gateway call to complete.
        Assert.True(ownership.TryResolveOrig(cancelClOrdId, out var origLinked));
        Assert.Equal(original, origLinked);
        Assert.True(bots.TryGetCancelMapping(cancelClOrdId, out var cm));
        Assert.Equal(original, cm.OriginalInternalClOrdId);
        Assert.Equal(77UL, cm.ExternalCancelClOrdId);

        gateway.CancelGate.SetResult();
        var result = await pending;
        Assert.Equal(OrderCancelResultKind.Accepted, result.Kind);
        Assert.Equal(cancelClOrdId, result.CancelClOrdId);
    }

    [Fact]
    public async Task CancelAsync_WithoutBotOrigin_DoesNotTouchBotMappingRegistry()
    {
        // REST DELETE path: the registry must not pick up REST-origin
        // cancels. The OrderCancelService still works without a registry,
        // and (when one is provided) leaves it untouched for non-bot calls.
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var gateway = new RecordingGateway();
        var bots = new InMemoryUserBotOrderMappingRegistry();

        const ulong original = 100UL;
        Assert.True(book.TryAdd(Working(original)));
        ownership.Register(original, Owner);

        var sut = new OrderCancelService(clOrdIds, ownership, book, gateway, dispatcher,
            NullLogger<OrderCancelService>.Instance, bots);

        var result = await sut.CancelAsync(Owner, original, CancellationToken.None,
            botOrigin: null);

        Assert.Equal(OrderCancelResultKind.Accepted, result.Kind);
        Assert.Empty(bots.SnapshotCancels());
    }

    [Fact]
    public async Task CancelAsync_NotFoundOrCrossOwner_ReturnsNotFound_NoWalNoGateway()
    {
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var gateway = new RecordingGateway();
        var bots = new InMemoryUserBotOrderMappingRegistry();

        var sut = new OrderCancelService(clOrdIds, ownership, book, gateway, dispatcher,
            NullLogger<OrderCancelService>.Instance, bots);

        // Unknown id.
        Assert.Equal(OrderCancelResultKind.NotFound,
            (await sut.CancelAsync(Owner, 100UL, CancellationToken.None)).Kind);

        // Cross-owner: order exists but is owned by someone else — must
        // surface as NotFound (info disclosure boundary).
        Assert.True(book.TryAdd(Working(101UL)));
        ownership.Register(101UL, Owner);
        Assert.Equal(OrderCancelResultKind.NotFound,
            (await sut.CancelAsync(new EndClientId("mallory"), 101UL, CancellationToken.None)).Kind);

        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task CancelAsync_ClosedRecoveryGate_PrecedesMissingOrderLookup()
    {
        var gateway = new RecordingGateway();
        var sut = new OrderCancelService(
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            new WorkingOrderBook(),
            gateway,
            new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance,
            outboundRecovery: new ClosedRecoveryGate());

        var result = await sut.CancelAsync(
            Owner,
            999UL,
            CancellationToken.None,
            firmId: "FIRM");

        Assert.Equal(OrderCancelResultKind.ReconciliationRequired, result.Kind);
        Assert.Empty(gateway.Calls);
    }

    private sealed class ClosedRecoveryGate : IOutboundRecoveryGate
    {
        public OutboundRecoveryPhase Phase => OutboundRecoveryPhase.RestoringPersistence;
        public bool IsClassificationComplete => false;
        public bool IsReady => false;
        public string? FailureReason => null;

        public IReadOnlyList<FirmOutboundRecoveryStatus> Snapshot() => [];

        public bool IsBusinessIngressOpen(string firmId) => false;

        public async ValueTask WaitUntilClassificationCompleteAsync(CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public async ValueTask WaitUntilBusinessIngressOpenAsync(
            string firmId,
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public async ValueTask WaitUntilAllRequiredBusinessIngressOpenAsync(
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    // ───── #768 code-review follow-up — mutationId/firmId threaded through
    // the pre-dispatch FailResolutionForReconciliation paths ─────

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }
        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public CapturingLogger(CapturingLoggerProvider owner) { _owner = owner; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _owner.Records.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class NoOpReplaceMargin : IReplaceMarginCoordinator
    {
        public Task<RiskDecision> PrepareReplaceAsync(ulong _, ulong __, EndClientId ___, decimal ____, CancellationToken _____)
            => Task.FromResult(RiskDecision.Approve);
        public void CommitReplace(ulong _, ulong __, decimal ___) { }
        public void AbortReplace(ulong _) { }
    }

    private sealed class RecordingDrainController : Lifecycle.IDrainController
    {
        public bool IsDraining { get; private set; }
        public void BeginDrain(string reason) => IsDraining = true;
    }

    /// <summary>
    /// Mirrors <c>OrderIdempotencyEndpointTests.RejectingApprovalStore</c>:
    /// throws <see cref="WalBackpressureException"/> only when appending
    /// the outbound approval commit, so the cancel path's "approval not
    /// committed" branch is deterministically reachable while the earlier
    /// <c>OrderCancelRequestedEvent</c> append still succeeds.
    /// </summary>
    private sealed class RejectingApprovalStore : IEventStore
    {
        private long _seq;
        public long CurrentSeq => _seq;
        public long LastCommittedSeq => _seq;
        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            if (evt is OutboundApprovedEvent)
                throw new WalBackpressureException("approval commit rejected");
            return ++_seq;
        }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask FlushThroughAsync(long seq, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AeadOutboundCommandProtector CreateCancelTestProtector() =>
        new(
            new OutboundCommandProtectionOptions
            {
                ActiveKeyId = "test",
                ActiveKeyVersion = 1,
                Keys =
                [
                    new OutboundCommandProtectionKeyOptions
                    {
                        KeyId = "test",
                        Version = 1,
                        KeyBase64 = Convert.ToBase64String(
                            System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(
                                    "order-cancel-service-tests"))),
                    },
                ],
            });

    [Fact]
    public async Task CancelAsync_OutboundApprovalNotCommitted_LogsMutationFirmClOrdId_PreservesMutationId()
    {
        // #768 code-review follow-up (3): the pre-dispatch
        // FailResolutionForReconciliation path must carry the real
        // mutationId/firmId into both the critical log and the
        // returned OrderCancelResult, not defaults. Force the
        // "approval not committed" branch via a WAL store that rejects
        // the OutboundApprovedEvent append.
        const string firm = "FIRM01";
        var orig = new Order(910_001UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, firm);
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(orig));
        ownership.Register(orig.ClOrdId, Owner);

        var gateway = new RecordingGateway();
        var protector = CreateCancelTestProtector();
        var ledger = new OutboundMutationLedger(protector);
        var approvalFactory = new CancelReplaceApprovalFactory(protector, outboundLedger: ledger);
        var rejectingStore = new RejectingApprovalStore();
        var dispatcher = new EventDispatcher(rejectingStore);
        var coordinator = new CancelReplaceOutboundCoordinator(
            ledger,
            new OutboundProcessEpoch(),
            protector,
            gateway,
            dispatcher,
            book,
            clOrdIds,
            ownership,
            new PendingCancelRegistry(),
            new PendingReplacementRegistry(),
            new NoOpReplaceMargin(),
            new RecordingDrainController(),
            NullLogger<CancelReplaceOutboundCoordinator>.Instance);

        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var svcLogger = loggerFactory.CreateLogger<OrderCancelService>();

        var sut = new OrderCancelService(
            clOrdIds, ownership, book, gateway, dispatcher, svcLogger,
            outboundLedger: ledger,
            approvalFactory: approvalFactory,
            outboundCoordinator: coordinator);

        var result = await sut.CancelAsync(
            Owner, orig.ClOrdId, CancellationToken.None, firmId: firm);

        Assert.Equal(OrderCancelResultKind.ReconciliationRequired, result.Kind);
        Assert.Equal("outbound_cancel_approval_not_committed", result.Reason);
        Assert.NotEqual(default, result.MutationId);

        var critical = Assert.Single(
            loggerProvider.Records, r => r.Level == LogLevel.Critical);
        Assert.Contains(result.MutationId.ToString(), critical.Message, StringComparison.Ordinal);
        Assert.Contains(firm, critical.Message, StringComparison.Ordinal);
    }
}
