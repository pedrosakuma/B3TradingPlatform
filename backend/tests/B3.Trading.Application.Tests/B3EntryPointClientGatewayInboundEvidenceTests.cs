using System.Net;

using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Sdk = B3.EntryPoint.Client;

namespace B3.Trading.Application.Tests;

public sealed class B3EntryPointClientGatewayInboundEvidenceTests
{
    [Fact]
    public async Task NotAppliedSubscription_StampsConfiguredIdentity_AndDropsStaleGeneration()
    {
        var now = new DateTimeOffset(2026, 7, 18, 15, 0, 0, TimeSpan.Zero);
        var first = new TestRetransmitHandler();
        var second = new TestRetransmitHandler();
        IRetransmitRequestHandler current = first;
        var client = new Sdk.EntryPointClient(new Sdk.EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
            SessionId = 42,
            SessionVerId = 7,
            EnteringFirm = 9,
            Credentials = Sdk.EntryPointClientOptions.AccessKey(
                "0123456789ABCDEF0123456789ABCDEF"),
            TerminateOnDispose = false,
        });
        await using var gateway = new B3EntryPointClientGateway(
            client,
            "FIRM-A",
            initialSessionVerId: 7,
            NullLogger<B3EntryPointClientGateway>.Instance,
            clock: new ManualTimeProvider(now),
            terminateOnShutdown: false,
            connectedTestHook: () => { },
            connectAsyncOverride: _ => Task.CompletedTask);
        gateway.ConfigureInboundEvidenceForTests(42, () => current);
        var observed = new List<NotAppliedEnvelope>();
        gateway.NotAppliedReceived += observed.Add;

        await gateway.ConnectAsync(CancellationToken.None);
        first.RaiseNotApplied(100, 3);

        current = second;
        await gateway.ConnectAsync(CancellationToken.None);
        first.RaiseNotApplied(200, 2);
        second.RaiseNotApplied(300, 4);

        Assert.Collection(
            observed,
            initial =>
            {
                Assert.Equal("FIRM-A", initial.FirmId);
                Assert.Equal(42UL, initial.SessionId);
                Assert.Equal(7U, initial.SessionVerId);
                Assert.Equal(100UL, initial.FromSeqNo);
                Assert.Equal(3U, initial.Count);
                Assert.Equal(now, initial.ObservedAtUtc);
            },
            refreshed =>
            {
                Assert.Equal(300UL, refreshed.FromSeqNo);
                Assert.Equal(4U, refreshed.Count);
            });
        Assert.Equal(0, first.RequestCount);
        Assert.Equal(0, second.RequestCount);
    }

    [Fact]
    public async Task RetransmissionWindow_MarksExecutionReportPossibleResend()
    {
        var retransmit = new TestRetransmitHandler();
        var releaseEvent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<ExecutionReportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateClient();
        await using var gateway = new B3EntryPointClientGateway(
            client,
            "FIRM-A",
            initialSessionVerId: 7,
            NullLogger<B3EntryPointClientGateway>.Instance,
            terminateOnShutdown: false,
            connectAsyncOverride: _ => Task.CompletedTask,
            eventStreamOverride: ct => Events(releaseEvent.Task, ct));
        gateway.ConfigureInboundEvidenceForTests(42, () => retransmit);
        gateway.ExecutionReportReceived += envelope => received.TrySetResult(envelope);

        await gateway.ConnectAsync(CancellationToken.None);
        retransmit.RaiseRetransmission(nextSeqNo: 90, count: 5);
        releaseEvent.SetResult();
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42UL, envelope.SessionId);
        Assert.Equal(7U, envelope.SessionVerId);
        Assert.Equal(92UL, envelope.InboundSeqNum);
        Assert.True(envelope.PossibleResend);
    }

    [Fact]
    public async Task ContinuingEventLoop_StampsSessionVersionPerEventAfterReconnect()
    {
        var retransmit = new TestRetransmitHandler();
        var releaseSecond = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<ExecutionReportEnvelope>();
        var client = CreateClient(sessionVerId: 1);
        await using var gateway = new B3EntryPointClientGateway(
            client,
            "FIRM-A",
            initialSessionVerId: 1,
            NullLogger<B3EntryPointClientGateway>.Instance,
            terminateOnShutdown: false,
            reconnectAsyncOverride: (_, _, _) => Task.FromResult(
                new Sdk.ReconnectOutcome(
                    Sdk.ReconnectKind.Reattached,
                    SessionVerId: 2,
                    ServerNextSeqNoExpected: 0,
                    ServerLastIncomingSeqNoSeen: 0,
                    RetransmitWindowReady: true)),
            connectAsyncOverride: _ => Task.CompletedTask,
            eventStreamOverride: ct => ReconnectEvents(
                releaseSecond.Task,
                ct));
        gateway.ConfigureInboundEvidenceForTests(42, () => retransmit);
        gateway.ExecutionReportReceived += envelope =>
        {
            lock (received)
            {
                received.Add(envelope);
                if (received.Count == 1)
                    firstReceived.TrySetResult();
                else if (received.Count == 2)
                    secondReceived.TrySetResult();
            }
        };

        await gateway.ConnectAsync(CancellationToken.None);
        await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await gateway.ReconnectLoopForTestsAsync(CancellationToken.None);
        releaseSecond.SetResult();
        await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Collection(
            received,
            first => Assert.Equal(1U, first.SessionVerId),
            second => Assert.Equal(2U, second.SessionVerId));
    }

    private static Sdk.EntryPointClient CreateClient(uint sessionVerId = 7) =>
        new(new Sdk.EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
            SessionId = 42,
            SessionVerId = sessionVerId,
            EnteringFirm = 9,
            Credentials = Sdk.EntryPointClientOptions.AccessKey(
                "0123456789ABCDEF0123456789ABCDEF"),
            TerminateOnDispose = false,
        });

    private static async IAsyncEnumerable<EntryPointEvent> Events(
        Task release,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await release.WaitAsync(ct);
        yield return new OrderAccepted
        {
            SeqNum = 92,
            SendingTime = new DateTimeOffset(
                2026, 7, 18, 15, 1, 0, TimeSpan.Zero),
            ClOrdID = new ClOrdID(101),
            OrderId = 700,
            OrderStatus = B3.EntryPoint.Client.Models.OrderStatus.New,
            SecurityId = 123,
            Side = Side.Buy,
        };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static async IAsyncEnumerable<EntryPointEvent> ReconnectEvents(
        Task releaseSecond,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return Accepted(seqNum: 1, clOrdId: 101);
        await releaseSecond.WaitAsync(ct);
        yield return Accepted(seqNum: 1, clOrdId: 102);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static OrderAccepted Accepted(ulong seqNum, ulong clOrdId) =>
        new()
        {
            SeqNum = seqNum,
            SendingTime = new DateTimeOffset(
                2026, 7, 18, 15, 2, 0, TimeSpan.Zero),
            ClOrdID = new ClOrdID(clOrdId),
            OrderId = clOrdId + 1000,
            OrderStatus = B3.EntryPoint.Client.Models.OrderStatus.New,
            SecurityId = 123,
            Side = Side.Buy,
        };

    private sealed class TestRetransmitHandler : IRetransmitRequestHandler
    {
        public int RequestCount { get; private set; }
        public event EventHandler<RetransmitRequestedEventArgs>? RetransmitRequested;
        public event EventHandler<RetransmissionEventArgs>? RetransmissionReceived;
        public event EventHandler<RetransmitRejectedEventArgs>? RetransmitRejected;
        public event EventHandler<NotAppliedEventArgs>? NotAppliedReceived;

        public Task RequestRetransmitAsync(
            ulong fromSeqNo,
            uint count,
            CancellationToken ct = default)
        {
            RequestCount++;
            return Task.CompletedTask;
        }

        public void RaiseNotApplied(ulong fromSeqNo, uint count) =>
            NotAppliedReceived?.Invoke(this, new NotAppliedEventArgs(fromSeqNo, count));

        public void RaiseRetransmission(ulong nextSeqNo, uint count) =>
            RetransmissionReceived?.Invoke(
                this,
                new RetransmissionEventArgs(nextSeqNo, count, DateTimeOffset.UnixEpoch));

        // Interface events are intentionally present but not raised by this test.
        public void KeepCompilerReferences()
        {
            _ = RetransmitRequested;
            _ = RetransmissionReceived;
            _ = RetransmitRejected;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
