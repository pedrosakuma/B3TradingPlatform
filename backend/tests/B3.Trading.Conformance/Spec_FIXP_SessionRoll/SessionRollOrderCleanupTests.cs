using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit.Sdk;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

public sealed class SessionRollOrderCleanupTests
{
    private static readonly AuthenticationHeaderValue UserAuth = new("Bearer", "user");
    private static readonly AuthenticationHeaderValue AdminAuth = new("Bearer", "admin");

    [Fact]
    public async Task StaleConflict_ClearsOverlay_RetriesCancel_AndWaitsForTerminalEr()
    {
        var state = new OrderState("Working", IsStale: true);
        var requests = new List<string>();
        using var http = CreateHttp((request, _) =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get)
                return Orders(state);
            if (request.Method == HttpMethod.Post)
            {
                state = state with { IsStale = false };
                return Response(HttpStatusCode.NoContent);
            }

            if (state.IsStale)
                return Response(HttpStatusCode.Conflict, new { error = "order is marked stale" });

            state = state with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            cleanup =>
            {
                cleanup.TrackOrder(101, "PETR4", "Buy", 30m, 100);
                return Task.CompletedTask;
            });

        Assert.Equal("Cancelled", state.Status);
        Assert.Equal(2, requests.Count(request => request == "DELETE /api/orders/101"));
        Assert.Contains(
            "POST /api/admin/firms/FIRM01/orders/101/clear-stale",
            requests);
    }

    [Fact]
    public async Task Cleanup_DiscoversOrdersWhoseSubmissionResponseWasLost()
    {
        var states = new Dictionary<ulong, OrderState>();
        var deleted = new List<ulong>();
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
                return Orders(states);

            deleted.Add(clOrdId);
            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        await SessionRollSpecSupport.RunWithOrderCleanupAsync(
            http,
            UserAuth,
            AdminAuth,
            _ =>
            {
                states[150] = new OrderState("Working", IsStale: false);
                return Task.CompletedTask;
            });

        Assert.Equal([150UL], deleted);
        Assert.Equal("Cancelled", states[150].Status);
    }

    [Fact]
    public async Task PrimaryFailure_IsPreserved_AndCleanupAttemptsEveryTrackedOrder()
    {
        var primary = new XunitException("primary scenario assertion");
        var restoration = new InvalidOperationException("recovery restoration fault");
        var deleted = new List<ulong>();
        var states = new Dictionary<ulong, OrderState>
        {
            [201] = new("Working", IsStale: false),
            [202] = new("Working", IsStale: false),
        };
        using var http = CreateHttp((request, clOrdId) =>
        {
            if (request.Method == HttpMethod.Get)
                return Orders(states);

            deleted.Add(clOrdId);
            if (clOrdId == 201)
                return Response(HttpStatusCode.InternalServerError, new { error = "cleanup fault" });

            states[clOrdId] = states[clOrdId] with { Status = "Cancelled" };
            return Response(HttpStatusCode.NoContent);
        });

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            SessionRollSpecSupport.RunWithOrderCleanupAsync(
                http,
                UserAuth,
                AdminAuth,
                cleanup =>
                {
                    cleanup.TrackOrder(201, "PETR4", "Buy", 30m, 100);
                    cleanup.TrackOrder(202, "VALE3", "Sell", 60m, 100);
                    throw primary;
                },
                beforeOrderCleanup: () => Task.FromException(restoration)));

        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(restoration, aggregate.InnerExceptions[1].InnerException);
        Assert.Contains("tracked Buy order 201", aggregate.InnerExceptions[2].Message);
        Assert.Equal([201UL, 202UL], deleted);
        Assert.Equal("Cancelled", states[202].Status);
    }

    [Fact]
    public async Task CompletedScenario_WithCleanupFailure_ReportsTheCleanupFailure()
    {
        using var http = CreateHttp((request, _) =>
            request.Method == HttpMethod.Get
                ? Orders(new OrderState("Working", IsStale: false))
                : Response(HttpStatusCode.NotFound));

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            SessionRollSpecSupport.RunWithOrderCleanupAsync(
                http,
                UserAuth,
                AdminAuth,
                cleanup =>
                {
                    cleanup.TrackOrder(301, "ITUB4", "Buy", 25m, 100);
                    return Task.CompletedTask;
                }));

        Assert.Single(aggregate.InnerExceptions);
        Assert.Contains("venue terminality", aggregate.InnerExceptions[0].InnerException!.Message);
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

    private static HttpResponseMessage Orders(OrderState state) =>
        Orders(new Dictionary<ulong, OrderState> { [101] = state });

    private static HttpResponseMessage Orders(IReadOnlyDictionary<ulong, OrderState> states) =>
        Response(
            HttpStatusCode.OK,
            states.Select(entry => new
            {
                clOrdId = entry.Key.ToString(),
                status = entry.Value.Status,
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

    private sealed record OrderState(string Status, bool IsStale);
}
