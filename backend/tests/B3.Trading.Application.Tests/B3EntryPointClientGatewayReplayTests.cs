using System.Net;
using B3.EntryPoint.Client.TestPeer;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ClientClOrdId = B3.EntryPoint.Client.Models.ClOrdID;
using ClientSide = B3.EntryPoint.Client.Models.Side;
using Sdk = B3.EntryPoint.Client;

namespace B3.Trading.Application.Tests;

public sealed class B3EntryPointClientGatewayReplayTests
{
    private const string FirmId = "FIRM-A";
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 7, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReplayNotApplied_OnConfirmedSupersededGeneration_DoesNotFailCloseTerminalMutations()
    {
        var replay = TestPeerReplayScript.Create(defaultSessionId: 42u, defaultSessionVerId: 1u)
            .NegotiateAccept()
            .EstablishAck(nextSeqNo: 1u, lastIncomingSeqNo: 0u)
            .ExecutionReportAccepted((ClientClOrdId)101UL, orderId: 9_001UL, securityId: 4321UL, side: ClientSide.Buy, msgSeqNum: 1u)
            .ExecutionReportAccepted((ClientClOrdId)102UL, orderId: 9_002UL, securityId: 4321UL, side: ClientSide.Buy, msgSeqNum: 2u)
            .NotApplied(fromSeqNo: 1u, count: 2u, sessionVerId: 1u)
            .Build();

        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            ReplayScript = replay,
            Scenario = new DropAllScenario(),
        });
        peer.Start();

        var protector = CreateProtector();
        var ledger = new OutboundMutationLedger(protector);
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var processor = new ExecutionReportProcessor(
            ownership,
            book,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        await using var gateway = CreateGateway(peer.LocalEndpoint);
        var replayedExecutionReports = new List<ExecutionReportEnvelope>();
        gateway.ExecutionReportReceived += envelope =>
        {
            lock (replayedExecutionReports)
                replayedExecutionReports.Add(envelope);
        };
        using var router = new EntryPointExecutionReportRouter(
            gateway,
            processor,
            new EventDispatcher(new NullEventStore()),
            book,
            bookTop: null,
            drain: null,
            outboundLedger: ledger);

        await gateway.ConnectAsync(CancellationToken.None);

        var first = await SubmitTrackedOrderAsync(
            ledger,
            gateway,
            ownership,
            book,
            protector,
            clOrdId: 101UL,
            endClientId: "replay-alice",
            sequence: 1);
        var second = await SubmitTrackedOrderAsync(
            ledger,
            gateway,
            ownership,
            book,
            protector,
            clOrdId: 102UL,
            endClientId: "replay-bob",
            sequence: 2);

        Assert.Equal(1UL, first.OutboundSeqNum);
        Assert.Equal(2UL, second.OutboundSeqNum);
        Assert.All(
            ledger.SnapshotMutations(),
            mutation => Assert.Equal(
                OutboundMutationState.TransportWriteCompleted,
                mutation.State));

        Assert.Equal(2, await peer.AdvanceReplayAsync(frameCount: 2));
        await EventuallyAsync(() =>
        {
            lock (replayedExecutionReports)
                return replayedExecutionReports.Count == 2;
        });
        await EventuallyAsync(() =>
            AllMutationsAreVenueAcknowledged(ledger, first.MutationId, second.MutationId),
            () => DescribeLedger(ledger));

        ledger.ConfirmSessionRolled(FirmId, fromVerId: 1, toVerId: 2);

        Assert.Equal(1, await peer.AdvanceReplayAsync());
        await EventuallyAsync(() =>
            ledger.CaptureSnapshot().InboundEvidence.Any(e =>
                e.Kind == InboundVenueEvidenceKind.NotApplied));

        AssertTerminalWithoutReconciliation(ledger, first.MutationId);
        AssertTerminalWithoutReconciliation(ledger, second.MutationId);

        var notApplied = Assert.Single(
            ledger.CaptureSnapshot().InboundEvidence,
            evidence => evidence.Kind == InboundVenueEvidenceKind.NotApplied);
        Assert.Equal(InboundVenueEvidenceDisposition.SupersededSession, notApplied.Disposition);
        Assert.Equal([first.MutationId, second.MutationId], notApplied.MatchedMutationIds.OrderBy(id => id.Value).ToArray());
    }

    private static async Task<TrackedMutation> SubmitTrackedOrderAsync(
        OutboundMutationLedger ledger,
        B3EntryPointClientGateway gateway,
        OrderOwnershipMap ownership,
        WorkingOrderBook book,
        AeadOutboundCommandProtector protector,
        ulong clOrdId,
        string endClientId,
        int sequence)
    {
        var mutationId = new OutboundMutationId(Guid.Parse($"10000000-0000-0000-0000-{sequence:D12}"));
        var attemptId = new OutboundAttemptId(Guid.Parse($"20000000-0000-0000-0000-{sequence:D12}"));
        var order = new Order(
            clOrdId,
            new EndClientId(endClientId),
            "PETR4",
            4321UL,
            OrderSide.Buy,
            OrderType.Limit,
            100,
            10.25m,
            FirmId);
        Assert.True(book.TryAdd(order));
        ownership.Register(clOrdId, order.Owner);

        var canonical = new OutboundCanonicalCommand
        {
            ClOrdId = clOrdId,
            SecurityId = order.SecurityId,
            Symbol = order.Symbol,
            Side = order.Side.ToString(),
            OrderType = order.Type.ToString(),
            Quantity = order.Quantity,
            Price = order.Price,
            TimeInForce = order.TimeInForce.ToString(),
        };
        var sensitive = new SensitiveOutboundCommand
        {
            Account = $"ACC-{sequence}-SECRET",
            InvestorId = $"INV-{sequence}-SECRET",
            EndClientId = endClientId,
            CustomerIdentifier = $"DOC-{sequence}-SECRET",
            TradingSubAccount = $"SUB-{sequence}-SECRET",
        };
        var approval = OutboundApprovalFactory.Create(
            mutationId,
            FirmId,
            canonical,
            sensitive,
            [
                OutboundSensitiveFieldRef.Account,
                OutboundSensitiveFieldRef.InvestorId,
                OutboundSensitiveFieldRef.EndClientId,
                OutboundSensitiveFieldRef.CustomerIdentifier,
                OutboundSensitiveFieldRef.TradingSubAccount,
            ],
            protector,
            T0.AddSeconds(sequence),
            riskDecisionRef: $"risk-{sequence}",
            marginReservationRef: $"margin-{sequence}");
        ledger.Apply(new OutboundApprovedEvent
        {
            MutationId = mutationId,
            MutationKind = OutboundMutationKind.New,
            FirmId = FirmId,
            EndClientRef = protector.CreateStableEndClientRef(FirmId, endClientId),
            Origin = OutboundMutationOrigin.Rest,
            PrimaryClOrdId = clOrdId,
            RecordedAtUtc = T0.AddSeconds(sequence),
            Approval = approval,
            TimestampUtc = T0.AddSeconds(sequence),
        });
        ledger.Apply(new OutboundAttemptIntentPreparedEvent
        {
            MutationId = mutationId,
            AttemptId = attemptId,
            AttemptNo = 1,
            ClOrdId = clOrdId,
            ProcessEpochId = new ProcessEpochId(Guid.Parse($"30000000-0000-0000-0000-{sequence:D12}")),
            IntentPreparedAtUtc = T0.AddSeconds(sequence).AddMilliseconds(1),
            TimestampUtc = T0.AddSeconds(sequence).AddMilliseconds(1),
        });

        var receipt = await gateway.SubmitWithReceiptAsync(
            order,
            (frame, _) =>
            {
                ledger.Apply(new OutboundFramePreparedEvent
                {
                    MutationId = mutationId,
                    AttemptId = attemptId,
                    FirmId = frame.FirmId,
                    SessionId = frame.SessionId,
                    SessionVerId = frame.SessionVerId,
                    OutboundSeqNum = frame.OutboundSeqNum,
                    EncodedFrameSha256 = frame.EncodedFrameSha256,
                    PreparedAtUtc = T0.AddSeconds(sequence).AddMilliseconds(2),
                    TimestampUtc = T0.AddSeconds(sequence).AddMilliseconds(2),
                });
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        ledger.Apply(new OutboundTransportWriteCompletedEvent
        {
            MutationId = mutationId,
            AttemptId = attemptId,
            CompletedAtUtc = T0.AddSeconds(sequence).AddMilliseconds(3),
            GatewayReceiptVersion = receipt.Version,
            TimestampUtc = T0.AddSeconds(sequence).AddMilliseconds(3),
        });
        return new TrackedMutation(mutationId, receipt.Frame.OutboundSeqNum);
    }

    private static B3EntryPointClientGateway CreateGateway(IPEndPoint endpoint)
    {
        var options = new Sdk.EntryPointClientOptions
        {
            Endpoint = endpoint,
            SessionId = 42,
            SessionVerId = 1,
            EnteringFirm = 9,
            Credentials = Sdk.EntryPointClientOptions.AccessKey(
                "0123456789ABCDEF0123456789ABCDEF"),
            TerminateOnDispose = false,
        };
        return new B3EntryPointClientGateway(
            new Sdk.EntryPointClient(options),
            FirmId,
            initialSessionVerId: 1,
            NullLogger<B3EntryPointClientGateway>.Instance,
            new B3EntryPointClientGateway.OutboundTestOverrides(),
            terminateOnShutdown: false,
            sessionId: 42);
    }

    private static AeadOutboundCommandProtector CreateProtector() =>
        new(
            Options.Create(
                new OutboundCommandProtectionOptions
                {
                    ActiveKeyId = "key-a",
                    ActiveKeyVersion = 1,
                    StableReferenceKeyId = "key-a",
                    StableReferenceKeyVersion = 1,
                    Keys =
                    [
                        new OutboundCommandProtectionKeyOptions
                        {
                            KeyId = "key-a",
                            Version = 1,
                            KeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
                        },
                    ],
                }),
            new CryptographicOutboundNonceSource());

    private static bool AllMutationsAreVenueAcknowledged(
        OutboundMutationLedger ledger,
        params OutboundMutationId[] ids) =>
        ids.All(id =>
            ledger.TryGet(id, out var mutation)
            && mutation!.State == OutboundMutationState.VenueAcknowledged
            && !mutation.RequiresReconciliation);

    private static void AssertTerminalWithoutReconciliation(
        OutboundMutationLedger ledger,
        OutboundMutationId id)
    {
        Assert.True(ledger.TryGet(id, out var mutation));
        Assert.Equal(OutboundMutationState.VenueAcknowledged, mutation!.State);
        Assert.False(mutation.RequiresReconciliation);
        Assert.Null(Assert.Single(mutation.Attempts).AmbiguityReason);
    }

    private static async Task EventuallyAsync(
        Func<bool> condition,
        Func<string>? describeFailure = null,
        int maxAttempts = 200)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.True(condition(), describeFailure?.Invoke());
    }

    private static string DescribeLedger(OutboundMutationLedger ledger) =>
        string.Join(
            Environment.NewLine,
            ledger.SnapshotMutations().Select(mutation =>
                $"{mutation.MutationId}: state={mutation.State} recon={mutation.RequiresReconciliation} "
                + $"attemptState={mutation.Attempts.LastOrDefault()?.AmbiguityReason} "
                + $"clOrd={mutation.Attempts.LastOrDefault()?.ClOrdId} "
                + $"frameSession={mutation.Attempts.LastOrDefault()?.FramePrepared?.SessionId} "
                + $"frameVer={mutation.Attempts.LastOrDefault()?.FramePrepared?.SessionVerId} "
                + $"frameSeq={mutation.Attempts.LastOrDefault()?.FramePrepared?.OutboundSeqNum}")
                .Concat(ledger.CaptureSnapshot().InboundEvidence.Select(evidence =>
                $"evidence kind={evidence.Kind} disposition={evidence.Disposition} clOrd={evidence.ClOrdId} "
                + $"msgKind={evidence.MessageKind} session={evidence.SessionId} ver={evidence.SessionVerId} "
                + $"inboundSeq={evidence.InboundSeqNum} matched={string.Join(',', evidence.MatchedMutationIds.Select(id => id.Value))}")));

    private sealed record TrackedMutation(
        OutboundMutationId MutationId,
        ulong OutboundSeqNum);

    private sealed class DropAllScenario : ITestPeerScenario
    {
        public NewOrderResponse OnNewOrder(NewOrderContext context) =>
            new NewOrderResponse.AcceptAsNew();

        public OutboundFrameAction OnOutboundFrame(OutboundFrameContext context) =>
            new OutboundFrameAction.Drop();
    }
}
