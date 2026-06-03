using B3.Trading.Application;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

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
        IConnectSessionRollReactor? reactor)
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
            connectSessionRollReactor: reactor);
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
}
