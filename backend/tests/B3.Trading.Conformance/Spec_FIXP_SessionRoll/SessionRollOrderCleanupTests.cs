using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Conformance.Infrastructure;
using Xunit.Sdk;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

public sealed class SessionRollOrderCleanupTests
{
    private static readonly AuthenticationHeaderValue UserAuth = new("Bearer", "user");
    private static readonly AuthenticationHeaderValue AdminAuth = new("Bearer", "admin");

    [Fact]
    public async Task StaleOrder_ClearsOverlay_ThenWaitsForTargetedCancelTerminalEr()
    {
        var states = new Dictionary<ulong, OrderState>
        {
            [101] = new("Working", IsStale: true),
        };
        var requests = new List<string>();
        using var http = CreateHttp((request, clOrdId) =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get)
                return GetResponse(request, states);
            if (request.Method == HttpMethod.Post &&
                request.RequestUri.AbsolutePath.EndsWith("/clear-stale", StringComparison.Ordinal))
            {
                states[101] = states[101] with { IsStale = false };
                return Response(HttpStatusCode.NoContent);
            }

            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            (_, _) => Task.CompletedTask,
            cleanup =>
            {
                cleanup.TrackOrder(101, "PETR4", "Buy", 30m, 100);
                return Task.CompletedTask;
            });

        Assert.Equal("Cancelled", states[101].Status);
        Assert.Contains("POST /api/admin/firms/FIRM01/orders/101/clear-stale", requests);
        Assert.Single(requests, request => request == "DELETE /api/orders/101");
        Assert.DoesNotContain("POST /api/orders", requests);
    }

    [Fact]
    public async Task CompetingBaselineOrder_IsUntouched_AndCleanupEmitsNoPostDrainTrade()
    {
        var states = new Dictionary<ulong, OrderState>
        {
            [100] = new("Working", IsStale: false, "PETR4", "Buy", 30m),
        };
        var deleted = new List<ulong>();
        var marketDataTradesAfterDrain = 0;
        var scenarioDrained = false;
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
                return GetResponse(request, states);
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath == "/api/orders")
            {
                if (scenarioDrained)
                    marketDataTradesAfterDrain++;
                return Response(HttpStatusCode.InternalServerError);
            }

            deleted.Add(clOrdId);
            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            (_, _) => Task.CompletedTask,
            cleanup =>
            {
                states[101] = new("Working", IsStale: false, "PETR4", "Buy", 30m);
                cleanup.TrackOrder(101, "PETR4", "Buy", 30m, 100);
                scenarioDrained = true;
                return Task.CompletedTask;
            });

        Assert.Equal([101UL], deleted);
        Assert.Equal("Working", states[100].Status);
        Assert.Equal("Cancelled", states[101].Status);
        Assert.Equal(0, marketDataTradesAfterDrain);
    }

    [Fact]
    public async Task TargetedCancelWithoutTerminalEr_ProvesSnapshotAbsence_AndRestales()
    {
        var states = new Dictionary<ulong, OrderState>
        {
            [175] = new("Working", IsStale: false),
        };
        var provedAbsent = new List<ulong>();
        var requests = new List<string>();
        using var http = CreateHttp((request, clOrdId) =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get)
                return GetResponse(request, states);
            if (request.Method == HttpMethod.Post &&
                request.RequestUri.AbsolutePath.EndsWith("/mark-stale", StringComparison.Ordinal))
            {
                states[clOrdId] = states[clOrdId] with { IsStale = true };
                return Response(HttpStatusCode.NoContent);
            }

            return Response(HttpStatusCode.NoContent);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            (venueOrderId, _) =>
            {
                provedAbsent.Add(venueOrderId!.Value);
                return Task.CompletedTask;
            },
            cleanup =>
            {
                cleanup.TrackOrder(175, "PETR4", "Buy", 30m, 100);
                return Task.CompletedTask;
            });

        Assert.Equal([9175UL], provedAbsent);
        Assert.True(states[175].IsStale);
        Assert.Single(requests, request => request == "DELETE /api/orders/175");
        Assert.DoesNotContain("POST /api/orders", requests);
    }

    [Fact]
    public async Task Cleanup_DiscoversOrderWhoseSubmissionResponseWasLost()
    {
        var states = new Dictionary<ulong, OrderState>();
        var deleted = new List<ulong>();
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
                return GetResponse(request, states);

            deleted.Add(clOrdId);
            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            (_, _) => Task.CompletedTask,
            _ =>
            {
                states[151] = new("Working", IsStale: false, "VALE3", "Sell", 60m);
                return Task.CompletedTask;
            });

        Assert.Equal([151UL], deleted);
        Assert.Equal("Cancelled", states[151].Status);
    }

    [Fact]
    public async Task Cleanup_WaitsForVenueAcknowledgementBeforeCancel()
    {
        var states = new Dictionary<ulong, OrderState>();
        var detailReads = 0;
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = GetResponse(request, states);
                if (request.RequestUri!.AbsolutePath.EndsWith("/155", StringComparison.Ordinal) &&
                    states[155].AcknowledgementPending)
                {
                    detailReads++;
                    states[155] = states[155] with { AcknowledgementPending = false };
                }
                return response;
            }

            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            (_, _) => Task.CompletedTask,
            cleanup =>
            {
                states[155] = new(
                    "Working",
                    IsStale: false,
                    AcknowledgementPending: true);
                cleanup.TrackOrder(155, "PETR4", "Buy", 30m, 100);
                return Task.CompletedTask;
            });

        Assert.Equal(1, detailReads);
        Assert.Equal("Cancelled", states[155].Status);
    }

    [Fact]
    public async Task AuthoritativeVenueAbsent_SkipsDeleteForPendingNew()
    {
        var states = new Dictionary<ulong, OrderState>();
        var deleted = new List<ulong>();
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
                return GetResponse(request, states);
            deleted.Add(clOrdId);
            return Response(HttpStatusCode.InternalServerError);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            (_, _) => Task.CompletedTask,
            _ =>
            {
                states[160] = new(
                    "PendingNew",
                    IsStale: false,
                    VenueAbsent: true);
                return Task.CompletedTask;
            });

        Assert.Empty(deleted);
        Assert.Equal("PendingNew", states[160].Status);
    }

    [Fact]
    public async Task ReopenedVenueAbsentEvidence_DoesNotBypassCleanupProof()
    {
        var states = new Dictionary<ulong, OrderState>();
        using var http = CreateHttp((request, _) =>
            request.Method == HttpMethod.Get
                ? GetResponse(request, states)
                : Response(HttpStatusCode.InternalServerError));

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            SessionRollSpecSupport.RunWithOrderCleanupAsync(
                http,
                UserAuth,
                AdminAuth,
                (_, _) => Task.CompletedTask,
                _ =>
                {
                    states[165] = new(
                        "PendingNew",
                        IsStale: false,
                        VenueAbsent: true,
                        ReconciliationRequired: true);
                    return Task.CompletedTask;
                }));

        Assert.Contains(
            "no acknowledged venue OrderId",
            aggregate.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VenueAbsentWithoutTerminalEr_ReportsFailure_AndAttemptsEveryTrackedOrder()
    {
        var states = new Dictionary<ulong, OrderState>
        {
            [201] = new("Working", IsStale: false),
            [202] = new("Working", IsStale: false),
        };
        var deleted = new List<ulong>();
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
                return GetResponse(request, states);

            deleted.Add(clOrdId);
            if (clOrdId == 201)
                return Response(HttpStatusCode.NotFound);
            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            SessionRollSpecSupport.RunWithOrderCleanupAsync(
                http,
                UserAuth,
                AdminAuth,
                (_, _) => Task.CompletedTask,
                cleanup =>
                {
                    cleanup.TrackOrder(201, "PETR4", "Buy", 30m, 100);
                    cleanup.TrackOrder(202, "VALE3", "Sell", 60m, 100);
                    return Task.CompletedTask;
                }));

        Assert.Equal([201UL, 202UL], deleted);
        Assert.Contains("venue absence cannot be proven", aggregate.ToString());
        Assert.Equal("Working", states[201].Status);
        Assert.Equal("Cancelled", states[202].Status);
    }

    [Fact]
    public async Task PrimaryFailure_IsPreserved_AndCleanupErrorsRemainObservable()
    {
        var primary = new XunitException("primary scenario assertion");
        var restoration = new InvalidOperationException("recovery restoration fault");
        var states = new Dictionary<ulong, OrderState>
        {
            [301] = new("Working", IsStale: false),
            [302] = new("Working", IsStale: false),
        };
        var deleted = new List<ulong>();
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
                return GetResponse(request, states);

            deleted.Add(clOrdId);
            if (clOrdId == 301)
                return Response(HttpStatusCode.InternalServerError, new { error = "cleanup fault" });
            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            SessionRollSpecSupport.RunWithOrderCleanupAsync(
                http,
                UserAuth,
                AdminAuth,
                (_, _) => Task.CompletedTask,
                cleanup =>
                {
                    cleanup.TrackOrder(301, "PETR4", "Buy", 30m, 100);
                    cleanup.TrackOrder(302, "VALE3", "Sell", 60m, 100);
                    throw primary;
                },
                beforeOrderCleanup: () => Task.FromException(restoration)));

        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(restoration, aggregate.InnerExceptions[1].InnerException);
        Assert.Contains("tracked Buy order 301", aggregate.InnerExceptions[2].Message);
        Assert.Equal([301UL, 302UL], deleted);
        Assert.Equal("Cancelled", states[302].Status);
    }

    [Fact]
    public void MatchingSnapshotVenueOrderLookup_IsExact()
    {
        const string snapshot =
            """
            {
              "Engine": {
                "Books": [
                  {
                    "Orders": [
                      { "OrderId": 9001, "ClOrdId": "101", "EnteringFirm": 100 },
                      { "OrderId": 9002, "ClOrdId": "102", "EnteringFirm": 200 }
                    ]
                  }
                ]
              }
            }
            """;

        Assert.True(DockerVenueTransportController.SnapshotContainsTrackedOrder(
            snapshot,
            venueOrderId: 9001,
            clOrdId: 0));
        Assert.False(DockerVenueTransportController.SnapshotContainsTrackedOrder(
            snapshot,
            venueOrderId: 9003,
            clOrdId: 0));
        Assert.True(DockerVenueTransportController.SnapshotContainsTrackedOrder(
            snapshot,
            venueOrderId: null,
            clOrdId: 101));
        Assert.False(DockerVenueTransportController.SnapshotContainsTrackedOrder(
            snapshot,
            venueOrderId: null,
            clOrdId: 102));
    }

    private static HttpClient CreateHttp(
        Func<HttpRequestMessage, ulong, HttpResponseMessage> respond) =>
        new(new DelegateHandler(request =>
        {
            var clOrdId = request.RequestUri!.Segments
                .Select(segment => segment.Trim('/'))
                .Select(segment => ulong.TryParse(segment, out var parsed) ? parsed : 0)
                .FirstOrDefault(parsed => parsed != 0);
            return respond(request, clOrdId);
        }))
        {
            BaseAddress = new Uri("http://conformance.test"),
        };

    private static HttpResponseMessage GetResponse(
        HttpRequestMessage request,
        IReadOnlyDictionary<ulong, OrderState> states)
    {
        if (request.RequestUri!.AbsolutePath == "/api/orders")
            return Orders(states);
        if (request.RequestUri.AbsolutePath == "/api/admin/outbound-mutations/")
        {
            return Response(
                HttpStatusCode.OK,
                new
                {
                    mutations = states.Select(entry => new
                    {
                        mutationId = entry.Key.ToString(),
                        kind = "new",
                        primaryClOrdId = entry.Key,
                        state = entry.Value.AcknowledgementPending
                            ? "transport_write_completed"
                            : entry.Value.ReconciliationRequired
                            ? "ambiguous"
                            : entry.Value.VenueAbsent
                            ? "operator_resolved"
                            : "venue_acknowledged",
                        requiresReconciliation = entry.Value.ReconciliationRequired,
                    }),
                });
        }

        var mutationId = request.RequestUri.Segments
            .Select(segment => segment.Trim('/'))
            .Select(segment => ulong.TryParse(segment, out var parsed) ? parsed : 0)
            .First(parsed => parsed != 0);
        var state = states[mutationId];
        return Response(
            HttpStatusCode.OK,
            new
            {
                resolution = state.VenueAbsent || state.AcknowledgementPending
                    ? null
                    : new { venueOrderId = state.VenueOrderId ?? mutationId + 9000 },
                operatorEvidence = state.VenueAbsent
                    ? new[] { new { decision = "venue_absent" } }
                    : [],
            });
    }

    private static HttpResponseMessage Orders(IReadOnlyDictionary<ulong, OrderState> states) =>
        Response(
            HttpStatusCode.OK,
            states.Select(entry => new
            {
                clOrdId = entry.Key.ToString(),
                status = entry.Value.Status,
                symbol = entry.Value.Symbol,
                side = entry.Value.Side,
                quantity = 100,
                price = entry.Value.Price,
                cumulativeQuantity = 0,
                isStale = entry.Value.IsStale,
                staleReason = entry.Value.IsStale ? "session_rolled:1-2" : null,
            }));

    private static HttpResponseMessage Response(HttpStatusCode status, object? body = null) =>
        new(status)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed record OrderState(
        string Status,
        bool IsStale,
        string Symbol = "PETR4",
        string Side = "Buy",
        decimal Price = 30m,
        ulong? VenueOrderId = null,
        bool VenueAbsent = false,
        bool ReconciliationRequired = false,
        bool AcknowledgementPending = false);
}
