using System.Buffers.Binary;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Framing;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

public class FixpOrderAdapterFailClosedTests
{
    [Fact]
    public async Task ColdStartRecovery_RejectsBusinessDispatchBeforeSubmission()
    {
        var credentialId = Guid.NewGuid();
        const ulong externalClOrdId = 77;
        var adapter = new FixpOrderAdapter(
            new SymbolDirectory(new SymbolDirectoryOptions()),
            submit: null!,
            cancel: null!,
            new InMemoryUserBotOrderMappingRegistry(),
            NullLogger.Instance,
            new ClosedRecoveryGate());
        var scope = new FixpConnectionScope(
            "conn-recovering",
            new BotSessionPrincipal("alice", credentialId, "cred-1", "bot", "FIRM-A"),
            new BotSessionState(credentialId, 10, 2, 0));
        var decoded = new DecodedNewOrderSingle
        {
            MsgSeqNum = 1,
            ClOrdId = externalClOrdId,
        };
        await using var stream = new MemoryStream();

        var outcome = await adapter.HandleNewOrderSingleAsync(
            stream, decoded, scope, CancellationToken.None);

        Assert.True(outcome.ShouldKeepSession);
        var reader = new SofhFrameReader();
        reader.Append(stream.ToArray());
        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal((ushort)BusinessMessageRejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(1005u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[32..]));
    }

    [Fact]
    public async Task NonDefaultCredentialFirm_FlowsIntoSubmittedOrder()
    {
        var credentialId = Guid.NewGuid();
        const ulong securityId = 4321;
        var gateway = new RecordingGateway();
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        var submit = new OrderSubmissionService(
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            new WorkingOrderBook(),
            gateway,
            new NoOpExecutionEventSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()),
            new NoOpMarginProvider(),
            new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            new EventDispatcher(new SyntheticTerminalRejectingStore()),
            new TestDrainController(),
            NullLogger<OrderSubmissionService>.Instance,
            botMappings: mappings);
        var adapter = new FixpOrderAdapter(
            new SymbolDirectory(new SymbolDirectoryOptions
            {
                SecurityIds = new Dictionary<string, ulong> { ["PETR4"] = securityId },
            }),
            submit,
            cancel: null!,
            mappings,
            NullLogger.Instance);
        var scope = new FixpConnectionScope(
            "conn-multifirm",
            new BotSessionPrincipal("alice", credentialId, "cred-mf", "bot", "BROKER-B"),
            new BotSessionState(credentialId, 10, 2, 0));
        var decoded = new DecodedNewOrderSingle
        {
            MsgSeqNum = 1,
            ClOrdId = 77,
            SecurityId = securityId,
            Side = Side.BUY,
            OrdType = OrdType.LIMIT,
            OrderQty = 100,
            PriceMantissa = (long)(30d * PriceOptional.Multiplier),
            TimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce.DAY,
        };

        await adapter.HandleNewOrderSingleAsync(
            new MemoryStream(), decoded, scope, CancellationToken.None);

        var order = Assert.IsType<Order>(gateway.Submitted);
        Assert.Equal("BROKER-B", order.FirmId);
    }

    [Fact]
    public async Task TombstonedExternalId_NewSessionRejectsBeforeInternalAllocation()
    {
        var credentialId = Guid.NewGuid();
        const ulong securityId = 4321;
        const ulong externalClOrdId = 77;
        var gateway = new RecordingGateway();
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var baselineClOrdIds = new ClOrdIdPrefixRegistry();
        var submit = new OrderSubmissionService(
            clOrdIds,
            new OrderOwnershipMap(),
            new WorkingOrderBook(),
            gateway,
            new NoOpExecutionEventSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()),
            new NoOpMarginProvider(),
            new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            new EventDispatcher(new SyntheticTerminalRejectingStore()),
            new TestDrainController(),
            NullLogger<OrderSubmissionService>.Instance,
            botMappings: mappings);
        var adapter = new FixpOrderAdapter(
            new SymbolDirectory(new SymbolDirectoryOptions
            {
                SecurityIds = new Dictionary<string, ulong> { ["PETR4"] = securityId },
            }),
            submit,
            cancel: null!,
            mappings,
            NullLogger.Instance);
        var principal = new BotSessionPrincipal(
            "alice", credentialId, "cred-1", "bot", "FIRM-A");
        var firstScope = new FixpConnectionScope(
            "conn-1",
            principal,
            new BotSessionState(credentialId, 10, 2, 0));
        var request = new DecodedNewOrderSingle
        {
            MsgSeqNum = 1,
            ClOrdId = externalClOrdId,
            SecurityId = securityId,
            Side = Side.BUY,
            OrdType = OrdType.LIMIT,
            OrderQty = 100,
            PriceMantissa = (long)(30d * PriceOptional.Multiplier),
            TimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce.DAY,
        };

        await adapter.HandleNewOrderSingleAsync(
            new MemoryStream(), request, firstScope, CancellationToken.None);

        Assert.Equal(1, gateway.SubmitCount);
        Assert.True(mappings.TryGetByExternal(
            credentialId, externalClOrdId, out var internalClOrdId));
        mappings.Reap(internalClOrdId);

        var secondScope = new FixpConnectionScope(
            "conn-2",
            principal,
            new BotSessionState(credentialId, 11, 9, 0));
        await using var response = new MemoryStream();
        await adapter.HandleNewOrderSingleAsync(
            response,
            new DecodedNewOrderSingle
            {
                MsgSeqNum = 88,
                ClOrdId = externalClOrdId,
                SecurityId = securityId,
                Side = Side.BUY,
                OrdType = OrdType.LIMIT,
                OrderQty = 100,
                PriceMantissa = (long)(30d * PriceOptional.Multiplier),
                TimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce.DAY,
            },
            secondScope,
            CancellationToken.None);

        Assert.Equal(1, gateway.SubmitCount);
        var owner = new EndClientId("bot:" + principal.CredShortId.ToLowerInvariant());
        _ = baselineClOrdIds.Generate(owner);
        Assert.Equal(baselineClOrdIds.Generate(owner), clOrdIds.Generate(owner));
        var reader = new SofhFrameReader();
        reader.Append(response.ToArray());
        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal((ushort)BusinessMessageRejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(1003u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[32..]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalPrePipelineReject_TombstonesExternalId(bool unknownSecurity)
    {
        var credentialId = Guid.NewGuid();
        const ulong securityId = 4321;
        const ulong externalClOrdId = 77;
        var gateway = new RecordingGateway();
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        var submit = new OrderSubmissionService(
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            new WorkingOrderBook(),
            gateway,
            new NoOpExecutionEventSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()),
            new NoOpMarginProvider(),
            new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            new EventDispatcher(new SyntheticTerminalRejectingStore()),
            new TestDrainController(),
            NullLogger<OrderSubmissionService>.Instance,
            botMappings: mappings);
        var adapter = new FixpOrderAdapter(
            new SymbolDirectory(new SymbolDirectoryOptions
            {
                SecurityIds = new Dictionary<string, ulong> { ["PETR4"] = securityId },
            }),
            submit,
            cancel: null!,
            mappings,
            NullLogger.Instance);
        var scope = new FixpConnectionScope(
            "conn-1",
            new BotSessionPrincipal("alice", credentialId, "cred-1", "bot", "FIRM-A"),
            new BotSessionState(credentialId, 10, 2, 0));
        var first = new DecodedNewOrderSingle
        {
            MsgSeqNum = 1,
            ClOrdId = externalClOrdId,
            SecurityId = unknownSecurity ? 999UL : securityId,
            Side = unknownSecurity ? Side.BUY : (Side)byte.MaxValue,
            OrdType = OrdType.LIMIT,
            OrderQty = 100,
            PriceMantissa = (long)(30d * PriceOptional.Multiplier),
            TimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce.DAY,
        };

        await adapter.HandleNewOrderSingleAsync(
            new MemoryStream(), first, scope, CancellationToken.None);

        Assert.NotNull(Assert.Single(
            mappings.SnapshotBusinessIdentities()).ResolvedAtUtc);
        await using var retryResponse = new MemoryStream();
        await adapter.HandleNewOrderSingleAsync(
            retryResponse,
            new DecodedNewOrderSingle
            {
                MsgSeqNum = 2,
                ClOrdId = externalClOrdId,
                SecurityId = securityId,
                Side = Side.BUY,
                OrdType = OrdType.LIMIT,
                OrderQty = 100,
                PriceMantissa = (long)(30d * PriceOptional.Multiplier),
                TimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce.DAY,
            },
            scope,
            CancellationToken.None);

        Assert.Equal(0, gateway.SubmitCount);
        var reader = new SofhFrameReader();
        reader.Append(retryResponse.ToArray());
        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal((ushort)BusinessMessageRejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(1003u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[32..]));
    }

    [Fact]
    public async Task UnknownOrderCancelReject_TombstonesCancelExternalId()
    {
        var credentialId = Guid.NewGuid();
        const ulong securityId = 4321;
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        var adapter = new FixpOrderAdapter(
            new SymbolDirectory(new SymbolDirectoryOptions
            {
                SecurityIds = new Dictionary<string, ulong> { ["PETR4"] = securityId },
            }),
            submit: null!,
            cancel: null!,
            mappings,
            NullLogger.Instance);
        var scope = new FixpConnectionScope(
            "conn-1",
            new BotSessionPrincipal("alice", credentialId, "cred-1", "bot", "FIRM-A"),
            new BotSessionState(credentialId, 10, 2, 0));
        var request = new DecodedOrderCancelRequest
        {
            MsgSeqNum = 1,
            ClOrdId = 78,
            OrigClOrdId = 77,
            SecurityId = securityId,
            Side = Side.BUY,
        };

        await adapter.HandleOrderCancelRequestAsync(
            new MemoryStream(), request, scope, CancellationToken.None);

        Assert.NotNull(Assert.Single(
            mappings.SnapshotBusinessIdentities()).ResolvedAtUtc);
        mappings.RegisterOrderInternal(100, credentialId, 77);
        await using var retryResponse = new MemoryStream();
        await adapter.HandleOrderCancelRequestAsync(
            retryResponse,
            new DecodedOrderCancelRequest
            {
                MsgSeqNum = 2,
                ClOrdId = 78,
                OrigClOrdId = 77,
                SecurityId = securityId,
                Side = Side.BUY,
            },
            scope,
            CancellationToken.None);

        var reader = new SofhFrameReader();
        reader.Append(retryResponse.ToArray());
        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal((ushort)BusinessMessageRejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(1003u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[32..]));
    }

    [Fact]
    public async Task TerminalPrePipelineReject_ResolvesIdentityBeforeWriteFailure()
    {
        var credentialId = Guid.NewGuid();
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        var adapter = new FixpOrderAdapter(
            new SymbolDirectory(new SymbolDirectoryOptions()),
            submit: null!,
            cancel: null!,
            mappings,
            NullLogger.Instance);
        var scope = new FixpConnectionScope(
            "conn-1",
            new BotSessionPrincipal("alice", credentialId, "cred-1", "bot", "FIRM-A"),
            new BotSessionState(credentialId, 10, 2, 0));

        await Assert.ThrowsAsync<IOException>(() =>
            adapter.HandleNewOrderSingleAsync(
                new ThrowingWriteStream(),
                new DecodedNewOrderSingle
                {
                    MsgSeqNum = 1,
                    ClOrdId = 77,
                    SecurityId = 999,
                    Side = Side.BUY,
                    OrdType = OrdType.LIMIT,
                    OrderQty = 100,
                    PriceMantissa = (long)(30d * PriceOptional.Multiplier),
                    TimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce.DAY,
                },
                scope,
                CancellationToken.None));

        var tombstone = Assert.Single(mappings.SnapshotBusinessIdentities());
        Assert.Equal(77UL, tombstone.ExternalClOrdId);
        Assert.NotNull(tombstone.ResolvedAtUtc);
    }

    [Fact]
    public async Task TombstonedCancelId_RejectsBeforeCancelPipeline()
    {
        var credentialId = Guid.NewGuid();
        const ulong securityId = 4321;
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        mappings.RegisterOrderInternal(100, credentialId, 77);
        Assert.Equal(
            BotBusinessIdentityClaimResult.Claimed,
            mappings.TryClaimBusinessIdentity(
                credentialId,
                78,
                OutboundMutationKind.Cancel,
                DateTimeOffset.UtcNow));
        var adapter = new FixpOrderAdapter(
            new SymbolDirectory(new SymbolDirectoryOptions
            {
                SecurityIds = new Dictionary<string, ulong> { ["PETR4"] = securityId },
            }),
            submit: null!,
            cancel: null!,
            mappings,
            NullLogger.Instance);
        var scope = new FixpConnectionScope(
            "conn-1",
            new BotSessionPrincipal("alice", credentialId, "cred-1", "bot", "FIRM-A"),
            new BotSessionState(credentialId, 10, 2, 0));
        await using var response = new MemoryStream();

        await adapter.HandleOrderCancelRequestAsync(
            response,
            new DecodedOrderCancelRequest
            {
                MsgSeqNum = 2,
                ClOrdId = 78,
                OrigClOrdId = 77,
                SecurityId = securityId,
                Side = Side.BUY,
            },
            scope,
            CancellationToken.None);

        var reader = new SofhFrameReader();
        reader.Append(response.ToArray());
        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal((ushort)BusinessMessageRejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(1003u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[32..]));
    }

    [Fact]
    public async Task ReconciliationRequired_EmitsNonTerminalBmrAndRequestsSessionClose()
    {
        var credentialId = Guid.NewGuid();
        const ulong externalClOrdId = 77;
        const ulong securityId = 4321;
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        var drain = new TestDrainController();
        var submit = new OrderSubmissionService(
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            new WorkingOrderBook(),
            new ThrowingGateway(),
            new NoOpExecutionEventSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()),
            new NoOpMarginProvider(),
            new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            new EventDispatcher(new SyntheticTerminalRejectingStore()),
            drain,
            NullLogger<OrderSubmissionService>.Instance,
            botMappings: mappings);
        var symbols = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = new Dictionary<string, ulong> { ["PETR4"] = securityId },
        });
        var adapter = new FixpOrderAdapter(
            symbols,
            submit,
            cancel: null!,
            mappings,
            NullLogger.Instance);
        var scope = new FixpConnectionScope(
            "conn-1",
            new BotSessionPrincipal("alice", credentialId, "cred-1", "bot", "FIRM-A"),
            new BotSessionState(credentialId, SessionId: 10, CurrentVer: 2, LastCheckpointedOutboundSeq: 0));
        var decoded = new DecodedNewOrderSingle
        {
            MsgSeqNum = 1,
            ClOrdId = externalClOrdId,
            SecurityId = securityId,
            Side = Side.BUY,
            OrdType = OrdType.LIMIT,
            OrderQty = 100,
            PriceMantissa = (long)(30d * PriceOptional.Multiplier),
            TimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce.DAY,
        };
        await using var stream = new MemoryStream();

        var outcome = await adapter.HandleNewOrderSingleAsync(
            stream, decoded, scope, CancellationToken.None);

        Assert.False(outcome.ShouldKeepSession);
        Assert.NotEqual(0UL, outcome.ReconciliationClOrdId);
        Assert.True(drain.IsDraining);
        Assert.True(mappings.TryGetByExternal(
            credentialId, externalClOrdId, out var mappedInternalClOrdId));
        Assert.Equal(outcome.ReconciliationClOrdId, mappedInternalClOrdId);

        var reader = new SofhFrameReader();
        reader.Append(stream.ToArray());
        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal((ushort)BusinessMessageRejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(externalClOrdId,
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload[24..]));
        Assert.Equal(1011u,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[32..]));
        Assert.False(reader.TryReadFrame(out _));
    }

    [Fact]
    public async Task CancelReconciliationRequired_UsesDedicatedRejectReason()
    {
        var credentialId = Guid.NewGuid();
        const ulong externalCancelClOrdId = 78;
        const ulong externalOrigClOrdId = 77;
        const ulong internalOrigClOrdId = 100;
        const ulong securityId = 4321;
        var mappings = new InMemoryUserBotOrderMappingRegistry();
        mappings.RegisterOrderInternal(internalOrigClOrdId, credentialId, externalOrigClOrdId);
        var drain = new TestDrainController();
        drain.BeginDrain("test");
        var cancel = new OrderCancelService(
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            new WorkingOrderBook(),
            new ThrowingGateway(),
            new EventDispatcher(new SyntheticTerminalRejectingStore()),
            NullLogger<OrderCancelService>.Instance,
            botMappings: mappings,
            reconciliationDrain: drain);
        var symbols = new SymbolDirectory(new SymbolDirectoryOptions
        {
            SecurityIds = new Dictionary<string, ulong> { ["PETR4"] = securityId },
        });
        var adapter = new FixpOrderAdapter(
            symbols,
            submit: null!,
            cancel,
            mappings,
            NullLogger.Instance);
        var scope = new FixpConnectionScope(
            "conn-1",
            new BotSessionPrincipal("alice", credentialId, "cred-1", "bot", "FIRM-A"),
            new BotSessionState(credentialId, SessionId: 10, CurrentVer: 2, LastCheckpointedOutboundSeq: 0));
        var decoded = new DecodedOrderCancelRequest
        {
            MsgSeqNum = 2,
            ClOrdId = externalCancelClOrdId,
            OrigClOrdId = externalOrigClOrdId,
            SecurityId = securityId,
            Side = Side.BUY,
        };
        await using var stream = new MemoryStream();

        await adapter.HandleOrderCancelRequestAsync(
            stream, decoded, scope, CancellationToken.None);

        var reader = new SofhFrameReader();
        reader.Append(stream.ToArray());
        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal((ushort)ExecutionReport_RejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(1011u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[44..]));
        Assert.False(reader.TryReadFrame(out _));
    }

    private sealed class SyntheticTerminalRejectingStore : IEventStore
    {
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            if (evt is ExecutionReportReceivedEvent { Synthetic: true })
                throw new WalBackpressureException("forced saturation");
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingGateway : IExchangeGateway
    {
        public Task SubmitAsync(Order order, CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("venue unavailable"));
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            B3.Trading.Domain.TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingGateway : IExchangeGateway
    {
        public Order? Submitted { get; private set; }
        public int SubmitCount { get; private set; }
        public Task SubmitAsync(Order order, CancellationToken ct)
        {
            Submitted = order;
            SubmitCount++;
            return Task.CompletedTask;
        }
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            B3.Trading.Domain.TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException(new IOException("connection closed"));
    }

    private sealed class NoOpExecutionEventSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private sealed class TestDrainController : IDrainController
    {
        public bool IsDraining { get; private set; }
        public void BeginDrain(string reason) => IsDraining = true;
    }

    private sealed class ClosedRecoveryGate : IOutboundRecoveryGate
    {
        public OutboundRecoveryPhase Phase => OutboundRecoveryPhase.RestoringPersistence;
        public bool IsClassificationComplete => false;
        public bool IsReady => false;
        public string? FailureReason => null;
        public IReadOnlyList<FirmOutboundRecoveryStatus> Snapshot() => [];
        public bool IsBusinessIngressOpen(string firmId) => false;
        public async ValueTask WaitUntilClassificationCompleteAsync(
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public async ValueTask WaitUntilBusinessIngressOpenAsync(
            string firmId,
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public async ValueTask WaitUntilAllRequiredBusinessIngressOpenAsync(
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
