using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #768. Non-accepted POST /api/orders results must be diagnosable from
/// product logs alone (TraceIdentifier + MutationId + FirmId + ClOrdId +
/// result kind/code + HTTP status), even if the caller never sees the
/// response. Accepted/normal traffic must stay quiet.
/// </summary>
public sealed class OrderFailureLoggingEndpointTests
{
    [Fact]
    public async Task GatewayFailure_LogsTraceIdMutationFirmClOrdIdKindAndStatus()
    {
        var capture = new CapturingLoggerProvider();
        static void ReplaceGateway(IServiceCollection services)
        {
            services.RemoveAll<IExchangeGateway>();
            services.AddSingleton<IExchangeGateway, FailingApiGateway>();
        }
        void RegisterCapture(IServiceCollection services) =>
            services.AddSingleton<ILoggerProvider>(capture);
        void ConfigureServices(IServiceCollection services)
        {
            ReplaceGateway(services);
            RegisterCapture(services);
        }

        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            ConfigureServices);
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        // The POST /api/orders path always dispatches through the durable
        // outbound coordinator, so a gateway attempt exception surfaces as
        // ReconciliationRequired (503) here, not the legacy-only
        // GatewayFailed/502 kind (see OrderSubmissionService's legacy
        // gateway catch, covered separately by
        // OrderSubmissionFailClosedTests.LegacyGatewayFailure_LogsMutationFirmAndClOrdId).
        using var response = await PostAsync(http, token, "gateway-failure-key", Body());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await ReadAsync(response);

        var entry = Assert.Single(
            capture.Entries,
            e => e.Category == "B3.Trading.Api.OrdersEndpoints" && e.Level == LogLevel.Warning);
        Assert.Contains(payload.MutationId, entry.Message);
        Assert.Contains(payload.ClOrdId, entry.Message);
        Assert.Contains("ReconciliationRequired", entry.Message);
        Assert.Contains("503", entry.Message);
        Assert.NotEmpty(entry.TraceId);

        // Credentials, request body, and plaintext end-client identity must
        // never appear in the correlation log line.
        Assert.DoesNotContain(token, entry.Message);
        Assert.DoesNotContain("PETR4", entry.Message);
    }

    [Fact]
    public async Task CancelFailure_LogsTraceIdMutationFirmClOrdIdOrigClOrdIdKindAndStatus()
    {
        // #768 code-review follow-up (1). DELETE /api/orders now shares the
        // same correlation-log helper as POST — an unknown ClOrdID (never
        // reaches mutation allocation) still logs OrigClOrdId/kind/status so
        // an operator can see the attempted target even without a
        // MutationId.
        var capture = new CapturingLoggerProvider();
        void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<ILoggerProvider>(capture);

        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            ConfigureServices);
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        const string unknownClOrdId = "918273";
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/orders/{unknownClOrdId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "cancel-failure-key");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var entry = Assert.Single(
            capture.Entries,
            e => e.Category == "B3.Trading.Api.OrdersEndpoints" && e.Level == LogLevel.Warning);
        Assert.Contains(unknownClOrdId, entry.Message);
        Assert.Contains("NotFound", entry.Message);
        Assert.Contains("404", entry.Message);
        Assert.Contains("DELETE /api/orders", entry.Message);
        Assert.NotEmpty(entry.TraceId);
        Assert.DoesNotContain(token, entry.Message);
    }

    [Fact]
    public async Task ReplaceFailure_LogsTraceIdMutationFirmClOrdIdOrigClOrdIdKindAndStatus()
    {
        // #768 code-review follow-up (1). PUT /api/orders shares the same
        // helper; OrigClOrdId is the URL's original ClOrdID.
        var capture = new CapturingLoggerProvider();
        void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<ILoggerProvider>(capture);

        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            ConfigureServices);
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        const string unknownClOrdId = "918274";
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/orders/{unknownClOrdId}")
        {
            Content = JsonContent.Create(new { quantity = 50, price = 31m }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "replace-failure-key");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var entry = Assert.Single(
            capture.Entries,
            e => e.Category == "B3.Trading.Api.OrdersEndpoints" && e.Level == LogLevel.Warning);
        Assert.Contains(unknownClOrdId, entry.Message);
        Assert.Contains("NotFound", entry.Message);
        Assert.Contains("404", entry.Message);
        Assert.Contains("PUT /api/orders", entry.Message);
        Assert.NotEmpty(entry.TraceId);
        Assert.DoesNotContain(token, entry.Message);
    }

    [Fact]
    public async Task ReplayedSubmissionFailure_LogsReplayedTrue_WithMutationAndFirm()
    {
        // #768 code-review follow-up (1). A replayed POST that resolves to
        // a terminal rejection (WAL rejected the approval commit — the
        // same fixture as
        // OrderIdempotencyEndpointTests.ApprovalCommitFailure_ReplayReturnsDurableNoWriteRejection)
        // still returns HTTP 202 (same "accepted for async processing,
        // ultimately rejected" contract as the live Rejected kind) but must
        // still be logged as a failure — replayed=True distinguishes it
        // from the live-path warning for the same MutationId.
        var store = new RejectingApprovalStore();
        var capture = new CapturingLoggerProvider();
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IEventStore>();
                services.AddSingleton<IEventStore>(store);
                services.RemoveAll<IEventStoreHealth>();
                services.AddSingleton<IEventStoreHealth>(store);
                services.AddSingleton<ILoggerProvider>(capture);
            });
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        using var first = await PostAsync(http, token, "replay-failure-key", Body());
        using var second = await PostAsync(http, token, "replay-failure-key", Body());
        var firstPayload = await ReadAsync(first);
        var secondPayload = await ReadAsync(second);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(firstPayload.MutationId, secondPayload.MutationId);

        var replayedEntry = Assert.Single(
            capture.Entries,
            e => e.Category == "B3.Trading.Api.OrdersEndpoints"
                 && e.Level == LogLevel.Warning
                 && e.Message.Contains("replayed=True"));
        Assert.Contains(secondPayload.MutationId, replayedEntry.Message);
        Assert.Contains(secondPayload.ClOrdId, replayedEntry.Message);
        Assert.NotEmpty(replayedEntry.TraceId);
        Assert.DoesNotContain(token, replayedEntry.Message);
    }

    [Fact]
    public async Task AcceptedSubmission_DoesNotLogAWarning()
    {
        var capture = new CapturingLoggerProvider();
        void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<ILoggerProvider>(capture);

        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            ConfigureServices);
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        using var response = await PostAsync(http, token, "accepted-key", Body());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.DoesNotContain(
            capture.Entries,
            e => e.Category == "B3.Trading.Api.OrdersEndpoints" &&
                 e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AcceptedReplace_DoesNotLogAWarning()
    {
        // #768 code-review follow-up (1). A successful PUT replace must
        // stay as quiet as a successful POST submit.
        var capture = new CapturingLoggerProvider();
        void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<ILoggerProvider>(capture);

        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            ConfigureServices);
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        using var posted = await PostAsync(http, token, "replace-accepted-post-key", Body());
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var postedPayload = await ReadAsync(posted);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/orders/{postedPayload.ClOrdId}")
        {
            Content = JsonContent.Create(new { quantity = 150, price = 30m }),
        };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        put.Headers.Add("Idempotency-Key", "replace-accepted-put-key");
        using var putResponse = await http.SendAsync(put);
        Assert.Equal(HttpStatusCode.Accepted, putResponse.StatusCode);

        Assert.DoesNotContain(
            capture.Entries,
            e => e.Category == "B3.Trading.Api.OrdersEndpoints" &&
                 e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AcceptedCancel_DoesNotLogAWarning()
    {
        // #768 code-review follow-up (1). A successful DELETE cancel must
        // stay as quiet as a successful POST submit.
        var capture = new CapturingLoggerProvider();
        void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<ILoggerProvider>(capture);

        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            ConfigureServices);
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        using var posted = await PostAsync(http, token, "cancel-accepted-post-key", Body());
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var postedPayload = await ReadAsync(posted);

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/orders/{postedPayload.ClOrdId}");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        delete.Headers.Add("Idempotency-Key", "cancel-accepted-delete-key");
        using var deleteResponse = await http.SendAsync(delete);
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);

        Assert.DoesNotContain(
            capture.Entries,
            e => e.Category == "B3.Trading.Api.OrdersEndpoints" &&
                 e.Level >= LogLevel.Warning);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient http,
        string token,
        string key,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders/")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return await http.SendAsync(request);
    }

    private static object Body() => new
    {
        symbol = "PETR4",
        securityId = 4321,
        side = "Buy",
        type = "Limit",
        quantity = 100,
        price = 30m,
        timeInForce = "Day",
    };

    private static async Task<ResponsePayload> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new ResponsePayload(
            json.GetProperty("mutationId").GetString()!,
            json.GetProperty("clOrdId").GetString()!);
    }

    private sealed record ResponsePayload(string MutationId, string ClOrdId);

    private sealed class FailingApiGateway : IExchangeGateway
    {
        public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("venue unavailable"));

        public Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken) =>
            Task.FromException<ExchangeGatewayReceipt>(
                new InvalidOperationException("venue unavailable"));

        public Task CancelAsync(
            Order order,
            ulong newClOrdId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors <c>OrderIdempotencyEndpointTests.RejectingApprovalStore</c>:
    /// throws <see cref="WalBackpressureException"/> only when appending
    /// the outbound approval commit, forcing the durable "approval not
    /// committed" / no-write-rejection replay path used by
    /// <see cref="ReplayedSubmissionFailure_LogsReplayedTrue_WithMutationAndFirm"/>.
    /// </summary>
    private sealed class RejectingApprovalStore : IEventStore, IEventStoreHealth
    {
        private long _seq;
        public long CurrentSeq => _seq;
        public long LastCommittedSeq => _seq;
        public bool IsHealthy => true;
        public Exception? TerminalFault => null;

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

    /// <summary>
    /// Captures rendered log lines across every category the host emits,
    /// keyed by category/level, mirroring the CapturingLogger idiom used in
    /// the Application test suite (formatter-rendered string, not raw
    /// structured properties).
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message, string TraceId)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            private readonly string _category;

            public CapturingLogger(CapturingLoggerProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                var traceId = ExtractTraceId(state);
                lock (_owner.Entries)
                {
                    _owner.Entries.Add((_category, logLevel, message, traceId));
                }
            }

            // TraceIdentifier is logged as a message placeholder ({TraceIdentifier}),
            // not as a distinct structured property on TState here — pull it back
            // out of the rendered message's structured key/value pairs when
            // TState implements IReadOnlyList<KeyValuePair<string, object?>>
            // (the standard formatted log-values shape).
            private static string ExtractTraceId<TState>(TState state)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
                {
                    foreach (var kvp in kvps)
                    {
                        if (kvp.Key == "TraceIdentifier" && kvp.Value is string traceId)
                            return traceId;
                    }
                }
                return string.Empty;
            }
        }
    }
}
