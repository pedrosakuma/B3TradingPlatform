using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.SampleBot;

internal interface ITradingPlatformRestClient
{
    Task<IReadOnlyList<SubAccountDto>> GetSubAccountsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TradingOrder>> GetOrdersAsync(CancellationToken cancellationToken);

    Task<RestCallResult<OrderMutationResponse>> SubmitLimitOrderAsync(
        SubmitOrderCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<RestCallResult<OrderMutationResponse>> CancelOrderAsync(
        string clOrdId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class TradingPlatformRestClient : ITradingPlatformRestClient
{
    private readonly HttpClient _httpClient;
    private readonly AuthenticatedSessionCache _sessionCache;

    public TradingPlatformRestClient(HttpClient httpClient, AuthenticatedSessionCache sessionCache)
    {
        _httpClient = httpClient;
        _sessionCache = sessionCache;
    }

    public async Task<IReadOnlyList<SubAccountDto>> GetSubAccountsAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, "/api/sub-accounts", cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SubAccountDto[]>(SampleBotJson.Options, cancellationToken)
            ?? Array.Empty<SubAccountDto>();
    }

    public async Task<IReadOnlyList<TradingOrder>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, "/api/orders", cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TradingOrder[]>(SampleBotJson.Options, cancellationToken)
            ?? Array.Empty<TradingOrder>();
    }

    public async Task<RestCallResult<OrderMutationResponse>> SubmitLimitOrderAsync(
        SubmitOrderCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "/api/orders", cancellationToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(
            new SubmitOrderRequest(
                command.Symbol,
                command.SecurityId,
                command.Side,
                Type: "Limit",
                command.Quantity,
                command.Price,
                command.SubAccountId),
            options: SampleBotJson.Options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadMutationResponseAsync(response, cancellationToken);
    }

    public async Task<RestCallResult<OrderMutationResponse>> CancelOrderAsync(
        string clOrdId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/orders/{clOrdId}", cancellationToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadMutationResponseAsync(response, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var session = await _sessionCache.GetAsync(cancellationToken);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return request;
    }

    private static async Task<RestCallResult<OrderMutationResponse>> ReadMutationResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
            return new RestCallResult<OrderMutationResponse>(response.StatusCode, null, null, null);

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(content, SampleBotJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new RestCallResult<OrderMutationResponse>(response.StatusCode, null, null, $"Invalid JSON response: {ex.Message}");
        }

        if (response.IsSuccessStatusCode)
        {
            var mutation = payload.Deserialize<OrderMutationResponse>(SampleBotJson.Options)
                ?? throw new InvalidOperationException("Order mutation response was empty.");
            return new RestCallResult<OrderMutationResponse>(response.StatusCode, mutation, null, null);
        }

        var errorCode = payload.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
            ? codeElement.GetString()
            : null;
        var errorMessage = payload.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;
        return new RestCallResult<OrderMutationResponse>(response.StatusCode, null, errorCode, errorMessage);
    }
}
