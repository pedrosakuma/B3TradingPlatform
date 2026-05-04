using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.Trading.DemoDriver;

/// <summary>
/// Thin HTTP client over the trading-host REST surface used by both the
/// submitter and injector workers. Caches the JWT internally and refreshes
/// transparently when /auth/login responses surface an expiresAt within the
/// next minute.
/// </summary>
internal sealed class TradingClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly BotCredential _creds;
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public TradingClient(HttpClient http, BotCredential creds)
    {
        _http = http;
        _creds = creds;
    }

    public string Username => _creds.Username;

    public async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromMinutes(1))
            return;

        var resp = await _http.PostAsJsonAsync("/auth/login",
            new LoginRequest(_creds.Username, _creds.Password), JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty /auth/login response");
        _token = payload.Token;
        _expiresAt = payload.ExpiresAt;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task<HealthResponse?> GetHealthAsync(CancellationToken ct)
    {
        var resp = await _http.GetAsync("/health", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<HealthResponse>(JsonOpts, ct);
    }

    public async Task<SubmitResult> SubmitOrderAsync(string symbol, string side, long qty, decimal price, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var resp = await _http.PostAsJsonAsync("/orders", new SubmitOrderRequest(
            Symbol: symbol,
            SecurityId: 0,
            Side: side,
            Type: "Limit",
            Quantity: qty,
            Price: price), JsonOpts, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            var body = await resp.Content.ReadFromJsonAsync<SubmitOrderResponse>(JsonOpts, ct);
            if (body is null) return SubmitResult.Failed("empty response");
            // The OrdersEndpoints surface returns 202 on both Accepted and
            // server-side Rejected paths (includes Status=Rejected). Don't
            // register rejected orders — late inject would be a bogus fill.
            if (string.Equals(body.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
                return SubmitResult.Rejected(body.ClOrdId, body.Reason ?? "rejected");
            return SubmitResult.Accepted(body.ClOrdId);
        }

        return SubmitResult.Failed($"status={(int)resp.StatusCode}");
    }

    public async Task<InjectResult> InjectErAsync(string clOrdId, string type, long? lastQty, decimal? lastPx, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        if (!ulong.TryParse(clOrdId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var clOrdIdNum))
            return InjectResult.Failed($"non-numeric clOrdId '{clOrdId}'");

        var resp = await _http.PostAsJsonAsync("/admin/simulator/er",
            new InjectErRequest(clOrdIdNum, type, lastQty, lastPx, RejectReason: null), JsonOpts, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            var body = await resp.Content.ReadFromJsonAsync<InjectErResponse>(JsonOpts, ct);
            if (body is null) return InjectResult.Failed("empty response");
            return InjectResult.Ok(body.LeavesQuantity, body.CumulativeQuantity);
        }

        // 404 = unknown clOrdId (race or already evicted). 400 = overfill /
        // bad request. Both are caller-evict signals.
        return InjectResult.Failed($"status={(int)resp.StatusCode}");
    }

    private sealed record LoginRequest(string Username, string Password);
    private sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
    private sealed record SubmitOrderRequest(string Symbol, uint SecurityId, string Side, string Type, long Quantity, decimal? Price);
    private sealed record SubmitOrderResponse(string ClOrdId, string? Status, string? Reason);
    // ClOrdId is serialized as a JSON number on the simulator endpoint
    // (server-side type is ulong, no ToString() roundtrip), so the typed
    // request/response use ulong rather than string.
    private sealed record InjectErRequest(ulong ClOrdId, string Type, long? LastQty, decimal? LastPx, string? RejectReason);
    private sealed record InjectErResponse(ulong ClOrdId, string ExecType, long LeavesQuantity, long CumulativeQuantity);
    public sealed record ExchangeBlock(string Mode, bool ReadyForOrders, int FirmCount);
}

internal sealed record HealthResponse(string Status, TradingClient.ExchangeBlock? Exchange);

internal readonly record struct SubmitResult(SubmitResultKind Kind, string ClOrdId, string? Reason)
{
    public static SubmitResult Accepted(string clOrdId) => new(SubmitResultKind.Accepted, clOrdId, null);
    public static SubmitResult Rejected(string clOrdId, string reason) => new(SubmitResultKind.Rejected, clOrdId, reason);
    public static SubmitResult Failed(string reason) => new(SubmitResultKind.Failed, string.Empty, reason);
}

internal enum SubmitResultKind { Accepted, Rejected, Failed }

internal readonly record struct InjectResult(bool Success, long LeavesQuantity, long CumulativeQuantity, string? Reason)
{
    public static InjectResult Ok(long leaves, long cum) => new(true, leaves, cum, null);
    public static InjectResult Failed(string reason) => new(false, 0, 0, reason);
}

