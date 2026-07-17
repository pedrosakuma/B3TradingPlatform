using B3.Trading.Application;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Up = B3.EntryPoint.Client;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #512. Gateway-side detection seam: after the initial ConnectAsync the
/// gateway reads the SDK's EFFECTIVE SessionVerId; if it advanced past the
/// resumed verId (cold-resume fallback bump), the gateway syncs
/// CurrentSessionVerId and hands the roll to the Application reactor BEFORE
/// the event loop / first snapshot. Exercises the internal
/// <c>ReconcileConnectSessionRoll</c> directly so no live FIXP peer is
/// needed.
/// </summary>
public class GatewayConnectSessionRollTests
{
    private sealed class SpyReactor : IConnectSessionRollReactor
    {
        public readonly List<(string Firm, uint From, uint To)> Calls = new();
        public void OnSessionRolled(string firmId, uint fromVerId, uint toVerId)
            => Calls.Add((firmId, fromVerId, toVerId));
    }

    private static B3EntryPointClientGateway BuildGateway(
        uint initialVerId,
        Func<uint>? provider,
        IConnectSessionRollReactor? reactor,
        Func<Up.ReconnectMode, Func<uint, uint>, CancellationToken, Task<Up.ReconnectOutcome>>? reconnectAsyncOverride = null,
        Action? connectedTestHook = null,
        Func<CancellationToken, Task>? connectAsyncOverride = null,
        Func<CancellationToken, IAsyncEnumerable<Up.Models.EntryPointEvent>>? eventStreamOverride = null,
        Func<CancellationToken, Task>? reResolveEndpoint = null)
    {
        var opts = new B3.EntryPoint.Client.EntryPointClientOptions
        {
            Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 65000),
            SessionId = 1u,
            SessionVerId = initialVerId,
            EnteringFirm = 1u,
            Credentials = B3.EntryPoint.Client.EntryPointClientOptions.AccessKey("0123456789ABCDEF0123456789ABCDEF"),
        };
        var client = new B3.EntryPoint.Client.EntryPointClient(opts);
        return new B3EntryPointClientGateway(
            client, "FIRM_A", initialVerId, NullLogger<B3EntryPointClientGateway>.Instance,
            effectiveSessionVerIdProvider: provider,
            connectSessionRollReactor: reactor,
            reconnectAsyncOverride: reconnectAsyncOverride,
            connectedTestHook: connectedTestHook,
            connectAsyncOverride: connectAsyncOverride,
            eventStreamOverride: eventStreamOverride,
            reResolveEndpoint: reResolveEndpoint);
    }

    [Fact]
    public void ReconcileConnectSessionRoll_VerIdAdvanced_SyncsAndInvokesReactor()
    {
        var reactor = new SpyReactor();
        var gw = BuildGateway(initialVerId: 8, provider: () => 9u, reactor: reactor);

        gw.ReconcileConnectSessionRoll();

        Assert.Equal(9u, gw.CurrentSessionVerId);
        var call = Assert.Single(reactor.Calls);
        Assert.Equal(("FIRM_A", 8u, 9u), call);
    }

    [Fact]
    public void ReconcileConnectSessionRoll_NoAdvance_IsNoOp()
    {
        var reactor = new SpyReactor();
        var gw = BuildGateway(initialVerId: 8, provider: () => 8u, reactor: reactor);

        gw.ReconcileConnectSessionRoll();

        Assert.Equal(8u, gw.CurrentSessionVerId);
        Assert.Empty(reactor.Calls);
    }

    [Fact]
    public void ReconcileConnectSessionRoll_NullProvider_IsNoOp()
    {
        var reactor = new SpyReactor();
        var gw = BuildGateway(initialVerId: 8, provider: null, reactor: reactor);

        gw.ReconcileConnectSessionRoll();

        Assert.Equal(8u, gw.CurrentSessionVerId);
        Assert.Empty(reactor.Calls);
    }

    [Fact]
    public void ReconcileConnectSessionRoll_AdvanceWithNullReactor_StillSyncsVerId()
    {
        var gw = BuildGateway(initialVerId: 8, provider: () => 12u, reactor: null);

        gw.ReconcileConnectSessionRoll();

        Assert.Equal(12u, gw.CurrentSessionVerId);
    }

    [Fact]
    public void ReconcileConnectSessionRoll_ReactorThrows_LeavesVerIdAtOldBaseline()
    {
        // #512 backstop: if the reap fails, the gateway must NOT publish the
        // bumped verId — leaving the snapshot baseline at the old value keeps
        // the next-restart boot reconcile able to re-detect the roll.
        var gw = BuildGateway(
            initialVerId: 8,
            provider: () => 9u,
            reactor: new ThrowingReactor());

        gw.ReconcileConnectSessionRoll(); // must not throw

        Assert.Equal(8u, gw.CurrentSessionVerId);
    }

    private sealed class ThrowingReactor : IConnectSessionRollReactor
    {
        public void OnSessionRolled(string firmId, uint fromVerId, uint toVerId)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void ReconcileReconnectSessionRoll_Renegotiated_Advanced_InvokesReactor_AndPublishesVerId()
    {
        var reactor = new SpyReactor();
        var gw = BuildGateway(initialVerId: 8, provider: null, reactor: reactor);

        gw.ReconcileReconnectSessionRoll(
            B3.EntryPoint.Client.ReconnectKind.Renegotiated, priorVerId: 8, effectiveVerId: 9);

        Assert.Equal(9u, gw.CurrentSessionVerId);
        var call = Assert.Single(reactor.Calls);
        Assert.Equal(("FIRM_A", 8u, 9u), call);
    }

    [Fact]
    public void ReconcileReconnectSessionRoll_Reattached_NoReactor_MirrorsVerId()
    {
        var reactor = new SpyReactor();
        var gw = BuildGateway(initialVerId: 8, provider: null, reactor: reactor);

        // Reattach preserves the verId (effective == prior); no ghosts.
        gw.ReconcileReconnectSessionRoll(
            B3.EntryPoint.Client.ReconnectKind.Reattached, priorVerId: 8, effectiveVerId: 8);

        Assert.Equal(8u, gw.CurrentSessionVerId);
        Assert.Empty(reactor.Calls);
    }

    [Fact]
    public void ReconcileReconnectSessionRoll_Reattached_Advanced_InvokesReactor_AndPublishesVerId()
    {
        var reactor = new SpyReactor();
        var gw = BuildGateway(initialVerId: 8, provider: null, reactor: reactor);

        gw.ReconcileReconnectSessionRoll(
            B3.EntryPoint.Client.ReconnectKind.Reattached, priorVerId: 8, effectiveVerId: 9);

        Assert.Equal(9u, gw.CurrentSessionVerId);
        var call = Assert.Single(reactor.Calls);
        Assert.Equal(("FIRM_A", 8u, 9u), call);
    }

    [Fact]
    public async Task ReconnectLoopAsync_FailedReconnectAttempt_DoesNotAdvancePriorBaselineBeforeSuccessfulReattach()
    {
        var reactor = new SpyReactor();
        var attempts = 0;
        var selectorInputs = new List<uint>();
        var gw = BuildGateway(
            initialVerId: 8,
            provider: null,
            reactor: reactor,
            reconnectAsyncOverride: (mode, selector, ct) =>
            {
                Assert.Equal(Up.ReconnectMode.EstablishReuseThenNegotiate, mode);
                attempts++;
                if (attempts == 1)
                {
                    selectorInputs.Add(selector(8));
                    throw new IOException("simulated reconnect failure after local session-ver bump");
                }

                return Task.FromResult(new Up.ReconnectOutcome(
                    Up.ReconnectKind.Reattached,
                    SessionVerId: 9,
                    ServerNextSeqNoExpected: 0,
                    ServerLastIncomingSeqNoSeen: 0,
                    RetransmitWindowReady: true));
            },
            connectedTestHook: static () => { });

        await gw.ReconnectLoopForTestsAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal([9u], selectorInputs);
        Assert.Equal(9u, gw.CurrentSessionVerId);
        var call = Assert.Single(reactor.Calls);
        Assert.Equal(("FIRM_A", 8u, 9u), call);
        Assert.False(gw.IsReconnecting);
    }

    [Fact]
    public void ReconcileReconnectSessionRoll_Renegotiated_NoAdvance_IsNoReactorCall()
    {
        // Defensive: Renegotiated but verId did not advance — treat as no roll.
        var reactor = new SpyReactor();
        var gw = BuildGateway(initialVerId: 8, provider: null, reactor: reactor);

        gw.ReconcileReconnectSessionRoll(
            B3.EntryPoint.Client.ReconnectKind.Renegotiated, priorVerId: 8, effectiveVerId: 8);

        Assert.Equal(8u, gw.CurrentSessionVerId);
        Assert.Empty(reactor.Calls);
    }

    [Fact]
    public void ReconcileReconnectSessionRoll_ReactorThrows_LeavesVerIdAtOldBaseline()
    {
        // Backstop: a reactor failure must NOT publish the bumped verId, so the
        // next-restart boot reconcile can re-detect the roll.
        var gw = BuildGateway(initialVerId: 8, provider: null, reactor: new ThrowingReactor());

        gw.ReconcileReconnectSessionRoll(
            B3.EntryPoint.Client.ReconnectKind.Renegotiated, priorVerId: 8, effectiveVerId: 9); // must not throw

        Assert.Equal(8u, gw.CurrentSessionVerId);
    }

    [Fact]
    public async Task EventLoopFault_MarksGatewayUnhealthy_AndStartsReconnect()
    {
        var reconnectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gw = BuildGateway(
            initialVerId: 8,
            provider: null,
            reactor: null,
            connectAsyncOverride: _ => Task.CompletedTask,
            eventStreamOverride: FaultedEvents,
            reconnectAsyncOverride: async (_, _, ct) =>
            {
                reconnectEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            });

        await gw.ConnectAsync(CancellationToken.None);
        await reconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("disconnected", gw.SessionStateTag);
        Assert.True(gw.IsReconnecting);
        await gw.DisposeAsync();
    }

    [Fact]
    public async Task ColdConnectAndReconnect_AreSerialized_AndDnsRunsInsideConnect()
    {
        var dnsCompleted = false;
        var connectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConnect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gw = BuildGateway(
            initialVerId: 8,
            provider: null,
            reactor: null,
            connectedTestHook: static () => { },
            reResolveEndpoint: _ =>
            {
                dnsCompleted = true;
                return Task.CompletedTask;
            },
            connectAsyncOverride: async ct =>
            {
                Assert.True(dnsCompleted);
                connectEntered.TrySetResult();
                await allowConnect.Task.WaitAsync(ct);
            },
            reconnectAsyncOverride: (_, _, _) =>
            {
                reconnectEntered.TrySetResult();
                return Task.FromResult(new Up.ReconnectOutcome(
                    Up.ReconnectKind.Reattached, 8, 0, 0, true));
            });

        var connect = gw.ConnectAsync(CancellationToken.None);
        await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reconnect = gw.ReconnectLoopForTestsAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(reconnectEntered.Task.IsCompleted);

        allowConnect.TrySetResult();
        await connect;
        await reconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await reconnect;
        await gw.DisposeAsync();
    }

    private static async IAsyncEnumerable<Up.Models.EntryPointEvent> FaultedEvents(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        throw new IOException("faulted event stream");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
