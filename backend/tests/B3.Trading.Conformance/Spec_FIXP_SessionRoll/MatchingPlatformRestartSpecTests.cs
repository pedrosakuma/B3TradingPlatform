using System.Net.Http.Headers;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

[Trait("Category", "Conformance")]
public class MatchingPlatformRestartSpecTests
{
    private static readonly TimeSpan MatchingRestartTimeout = TimeSpan.FromSeconds(60);

    [ConformanceFact(RequiresAdmin = true, RequiresSandboxMatching = true, RequiresDockerControl = true)]
    public async Task Restart_Renegotiates_SurvivingOrderFlaggedStale_BookSurvives_FreshTradingRecovers()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var docker = new DockerVenueTransportController();
        var survivorPrice = SessionRollSpecSupport.PriceNearUpperCollar(
            await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, "ITUB4"));
        var freshRoundTripPrice = SessionRollSpecSupport.PriceNearUpperCollar(
            await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(http, adminAuth, "PETR4"));

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            userAuth,
            adminAuth,
            (venueOrderId, clOrdId) => docker.WaitForVenueOrderAbsentAsync(
                venueOrderId,
                clOrdId,
                SessionRollSpecSupport.TradeTimeout),
            async cleanup =>
            {
                var before = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(http, adminAuth);
                var restingClOrdId = await cleanup.SubmitOrderAsync("ITUB4", survivorPrice);
                await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, restingClOrdId, order =>
                        order.Status == "Working" && !order.IsStale,
                    SessionRollSpecSupport.OrderTimeout,
                    "pre-restart order to reach Working before the matching-platform restart");

                await docker.RestartMatchingAsync(
                    MatchingRestartTimeout,
                    whileRestarting: async () =>
                    {
                        var stimulationDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                        while (DateTimeOffset.UtcNow < stimulationDeadline)
                        {
                            _ = await SessionRollSpecSupport.StimulateGatewayWriteAsync(
                                cleanup,
                                http,
                                userAuth,
                                "VALE3",
                                60.00m);
                            await Task.Delay(TimeSpan.FromMilliseconds(250));
                        }
                    });

                var after = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(
                    http, adminAuth, priorVerId: before.SessionVerId, expectAdvance: true);
                var staleOrder = await SessionRollSpecSupport.WaitForOrderAsync(http, userAuth, restingClOrdId, order =>
                        order.Status == "Working"
                        && order.IsStale
                        && order.StaleReason?.StartsWith("session_rolled:", StringComparison.Ordinal) == true,
                    SessionRollSpecSupport.ReconnectTimeout,
                    "pre-restart order to be marked stale after matching-platform renegotiation");

                Assert.True(after.SessionVerId > before.SessionVerId,
                    $"Expected sessionVerId to advance past {before.SessionVerId}, observed {after.SessionVerId}.");
                Assert.True(staleOrder.IsStale);
                Assert.StartsWith("session_rolled:", staleOrder.StaleReason);

                // Contract-level proof that matching's own book/WAL survived
                // the process restart: the pre-restart resting order is no
                // phantom. Once the FIXP session renegotiates, crossing it
                // from the host still yields a real terminal ER.
                var proofCrossStartedUtc = DateTimeOffset.UtcNow;
                var crossingClOrdId = await cleanup.SubmitOrderAsync(
                    "ITUB4",
                    survivorPrice,
                    side: "Sell");
                var filledResting = await SessionRollSpecSupport.WaitForOrderAsync(
                    http,
                    userAuth,
                    restingClOrdId,
                    order =>
                        order.Status == "Filled"
                        && order.CumulativeQuantity == SessionRollSpecSupport.RoundTripQuantity
                        && !order.IsStale
                        && order.StaleReason is null,
                    SessionRollSpecSupport.TradeTimeout,
                    "pre-restart stale order to fill and auto-clear its advisory stale flag");
                var filledCrossing = await SessionRollSpecSupport.WaitForOrderAsync(
                    http,
                    userAuth,
                    crossingClOrdId,
                    order =>
                        order.Status == "Filled"
                        && order.CumulativeQuantity == SessionRollSpecSupport.RoundTripQuantity,
                    SessionRollSpecSupport.TradeTimeout,
                    "restart-era crossing sell order to reach Filled");

                Assert.Equal(SessionRollSpecSupport.RoundTripQuantity, filledResting.CumulativeQuantity);
                Assert.Equal(SessionRollSpecSupport.RoundTripQuantity, filledCrossing.CumulativeQuantity);
                await docker.WaitForMarketDataTradeDrainAsync(
                    proofCrossStartedUtc,
                    SessionRollSpecSupport.TradeTimeout);

                await SessionRollSpecSupport.AssertPostRecoveryTradingRoundTripAsync(
                    cleanup,
                    http,
                    userAuth,
                    docker,
                    "PETR4",
                    freshRoundTripPrice,
                    SessionRollSpecSupport.RoundTripQuantity);
            });
    }
}
