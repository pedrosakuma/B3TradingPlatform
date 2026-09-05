using System.Net.Http.Headers;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

[Trait("Category", "Conformance")]
public class SuspendedTimeoutBoundarySpecTests
{
    // The venue terminates a FIXP session as idle after ~3x the client-
    // negotiated KeepAliveIntervalMs (see ExchangeOptions.KeepAliveIntervalMs,
    // default 15_000ms post-#792) of inbound silence during a transport
    // outage. WithinWindowDisconnect must stay comfortably under that
    // threshold (session reattaches, no roll); PastWindowDisconnect must
    // comfortably exceed it (session rolls/renegotiates). PastWindowDisconnect
    // was 5000ms pre-#792 when KeepAliveIntervalMs defaulted to 1000ms
    // (~3s threshold) -- it did not actually exercise the venue's separate
    // SuspendedTimeoutMs (5 min) mechanism the test name references; it
    // exercised the (far tighter) keepalive-lapse boundary instead. Bumped to
    // stay correct under the new default. WithinWindowDisconnect is left
    // unchanged at 1000ms -- it was already comfortably under both the old
    // and new thresholds.
    private static readonly TimeSpan WithinWindowDisconnect = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan PastWindowDisconnect = TimeSpan.FromMilliseconds(50_000);

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task WithinSuspendedTimeout_Reattaches_OrderSurvivesNoStaleFlag()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var docker = new DockerVenueTransportController();
        var petr4ReferencePrice = await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, "PETR4");
        var vale3ReferencePrice = await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, "VALE3");
        var restingPrice = SessionRollSpecSupport.PriceNearLowerCollar(petr4ReferencePrice);
        var roundTripPrice = SessionRollSpecSupport.PriceNearUpperCollar(petr4ReferencePrice);
        var probePrice = SessionRollSpecSupport.PriceNearLowerCollar(vale3ReferencePrice);

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            userAuth,
            adminAuth,
            (venueOrderId, clOrdId) => docker.IsVenueOrderPresentAsync(
                venueOrderId,
                clOrdId,
                SessionRollSpecSupport.TradeTimeout),
            (venueOrderId, clOrdId) => docker.WaitForVenueOrderAbsentAsync(
                venueOrderId,
                clOrdId,
                SessionRollSpecSupport.TradeTimeout),
            async cleanup =>
            {
                var before = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(http, adminAuth);
                var clOrdId = await cleanup.SubmitOrderAsync("PETR4", restingPrice);
                var probeClOrdId = await cleanup.SubmitOrderAsync(
                    "VALE3",
                    probePrice,
                    side: "Sell");
                await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, clOrdId, order =>
                        order.Status == "Working" && !order.IsStale,
                    SessionRollSpecSupport.OrderTimeout,
                    "order to reach Working before transport interruption");
                await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, probeClOrdId, order =>
                        order.Status == "Working" && !order.IsStale,
                    SessionRollSpecSupport.OrderTimeout,
                    "probe order to reach Working before transport interruption");

                await using (var detached = await docker.DisconnectMatchingAsync())
                {
                    // Start the idle-window clock only once the venue's
                    // network leg is actually severed -- DisconnectMatchingAsync
                    // spawns several sequential docker subprocesses before the
                    // partition takes effect, so timing from before that await
                    // would understate the real elapsed idle time the venue
                    // observes and erode the WithinWindowDisconnect/
                    // PastWindowDisconnect margins against the venue's ~3x
                    // KeepAliveIntervalMs idle-terminate threshold.
                    var disconnectStartedUtc = DateTimeOffset.UtcNow;
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    await SessionRollSpecSupport.StimulateGatewayWriteAsync(
                        http, userAuth, probeClOrdId);
                    await SessionRollSpecSupport.DelayUntilAsync(disconnectStartedUtc, WithinWindowDisconnect);
                    await detached.ReconnectAsync();
                }
                var after = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(
                    http,
                    adminAuth,
                    priorVerId: before.SessionVerId,
                    expectAdvance: false);
                var orderAfter = await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, clOrdId, order =>
                        order.Status == "Working" && !order.IsStale,
                    SessionRollSpecSupport.ReconnectTimeout,
                    "order to remain Working and non-stale after reattach");

                Assert.Equal(before.SessionVerId, after.SessionVerId);
                Assert.False(orderAfter.IsStale);
                Assert.Null(orderAfter.StaleReason);

                await SessionRollSpecSupport.AssertPostRecoveryTradingRoundTripAsync(
                    cleanup,
                    http,
                    userAuth,
                    docker,
                    "PETR4",
                    roundTripPrice,
                    SessionRollSpecSupport.RoundTripQuantity);
            });
    }

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task PastSuspendedTimeout_Renegotiates_SurvivingOrderFlaggedStale()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var docker = new DockerVenueTransportController();
        var vale3ReferencePrice = await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, "VALE3");
        var itub4ReferencePrice = await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, "ITUB4");
        var petr4ReferencePrice = await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, "PETR4");
        var restingPrice = SessionRollSpecSupport.PriceNearLowerCollar(vale3ReferencePrice);
        var roundTripPrice = SessionRollSpecSupport.PriceNearUpperCollar(itub4ReferencePrice);
        var probePrice = SessionRollSpecSupport.PriceNearLowerCollar(petr4ReferencePrice);

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            userAuth,
            adminAuth,
            (venueOrderId, clOrdId) => docker.IsVenueOrderPresentAsync(
                venueOrderId,
                clOrdId,
                SessionRollSpecSupport.TradeTimeout),
            (venueOrderId, clOrdId) => docker.WaitForVenueOrderAbsentAsync(
                venueOrderId,
                clOrdId,
                SessionRollSpecSupport.TradeTimeout),
            async cleanup =>
            {
                var before = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(http, adminAuth);
                var clOrdId = await cleanup.SubmitOrderAsync("VALE3", restingPrice);
                var probeClOrdId = await cleanup.SubmitOrderAsync("PETR4", probePrice);
                await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, clOrdId, order =>
                        order.Status == "Working" && !order.IsStale,
                    SessionRollSpecSupport.OrderTimeout,
                    "order to reach Working before transport interruption");
                await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, probeClOrdId, order =>
                        order.Status == "Working" && !order.IsStale,
                    SessionRollSpecSupport.OrderTimeout,
                    "probe order to reach Working before transport interruption");

                await using (var detached = await docker.DisconnectMatchingAsync())
                {
                    // See comment on the analogous block in
                    // WithinSuspendedTimeout_Reattaches_OrderSurvivesNoStaleFlag:
                    // start the idle-window clock only after the network is
                    // actually severed, not before the docker subprocess calls
                    // that sever it.
                    var disconnectStartedUtc = DateTimeOffset.UtcNow;
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    await SessionRollSpecSupport.StimulateGatewayWriteAsync(
                        http, userAuth, probeClOrdId);
                    await SessionRollSpecSupport.DelayUntilAsync(disconnectStartedUtc, PastWindowDisconnect);
                    await detached.ReconnectAsync();
                }
                var after = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(
                    http,
                    adminAuth,
                    priorVerId: before.SessionVerId,
                    expectAdvance: true);
                var orderAfter = await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, clOrdId, order =>
                        order.Status == "Working"
                        && order.IsStale
                        && order.StaleReason?.StartsWith("session_rolled:", StringComparison.Ordinal) == true,
                    SessionRollSpecSupport.ReconnectTimeout,
                    "order to be marked stale after renegotiated reconnect");

                Assert.True(after.SessionVerId > before.SessionVerId,
                    $"Expected sessionVerId to advance past {before.SessionVerId}, observed {after.SessionVerId}.");
                Assert.True(orderAfter.IsStale);
                Assert.StartsWith("session_rolled:", orderAfter.StaleReason);

                await SessionRollSpecSupport.AssertPostRecoveryTradingRoundTripAsync(
                    cleanup,
                    http,
                    userAuth,
                    docker,
                    "ITUB4",
                    roundTripPrice,
                    SessionRollSpecSupport.RoundTripQuantity);
            });
    }
}
