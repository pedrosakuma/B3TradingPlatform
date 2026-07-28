using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace B3.Trading.SampleBot;

public interface ISampleBotAuthProvider
{
    Task<AuthenticatedSession> AuthenticateAsync(CancellationToken cancellationToken);
}

internal sealed class SampleBotAuthProvider : ISampleBotAuthProvider
{
    private readonly HttpClient _httpClient;
    private readonly SampleBotOptions _options;
    private readonly ILogger<SampleBotAuthProvider> _logger;

    public SampleBotAuthProvider(
        HttpClient httpClient,
        Microsoft.Extensions.Options.IOptions<SampleBotOptions> options,
        ILogger<SampleBotAuthProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AuthenticatedSession> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var mode = _options.Auth.Mode;
        _logger.LogInformation("Authenticating SampleBot using mode {Mode}.", mode);
        return mode switch
        {
            SampleBotAuthMode.LocalPassword => await AuthenticateLocalPasswordAsync(cancellationToken),
            SampleBotAuthMode.ExternalExchange => await AuthenticateExternalExchangeAsync(cancellationToken),
            SampleBotAuthMode.InternalToken => AuthenticateInternalToken(),
            _ => throw new SampleBotAuthException($"Unsupported auth mode '{mode}'."),
        };
    }

    private async Task<AuthenticatedSession> AuthenticateLocalPasswordAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(_options.Auth.Username!.Trim(), _options.Auth.Password!),
            SampleBotJson.Options,
            cancellationToken);

        return await ReadInternalTokenResponseAsync(
            response,
            endpoint: "/api/auth/login",
            sourceMode: SampleBotAuthMode.LocalPassword,
            localPasswordMode: true,
            cancellationToken);
    }

    private async Task<AuthenticatedSession> AuthenticateExternalExchangeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/exchange");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Auth.ExternalAccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        return await ReadInternalTokenResponseAsync(
            response,
            endpoint: "/api/auth/exchange",
            sourceMode: SampleBotAuthMode.ExternalExchange,
            localPasswordMode: false,
            cancellationToken);
    }

    private AuthenticatedSession AuthenticateInternalToken()
    {
        var token = _options.Auth.InternalTradingToken!.Trim();
        var expiresAt = TryReadJwtExpiration(token);
        return new AuthenticatedSession(token, expiresAt, SampleBotAuthMode.InternalToken);
    }

    private static DateTimeOffset? TryReadJwtExpiration(string token)
    {
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var exp = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Exp)?.Value;
            return long.TryParse(exp, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : jwt.ValidTo == DateTime.MinValue ? null : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<AuthenticatedSession> ReadInternalTokenResponseAsync(
        HttpResponseMessage response,
        string endpoint,
        SampleBotAuthMode sourceMode,
        bool localPasswordMode,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            if (response.StatusCode == HttpStatusCode.OK)
                throw new SampleBotAuthException($"{endpoint} returned an empty success response.");
            throw new SampleBotAuthException($"{endpoint} failed: http_{(int)response.StatusCode} (HTTP {(int)response.StatusCode}).");
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(content, SampleBotJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            if (response.StatusCode == HttpStatusCode.OK)
                throw new SampleBotAuthException($"{endpoint} returned a non-JSON response.");
            throw new SampleBotAuthException($"{endpoint} failed: http_{(int)response.StatusCode} (HTTP {(int)response.StatusCode}).");
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (TryReadLoginResponse(payload, out var success))
                return new AuthenticatedSession(success.Token, success.ExpiresAt, sourceMode);

            if (localPasswordMode && TryReadTwoFactorRequired(payload, out var twoFactor))
            {
                var factors = twoFactor.Factors.Count == 0
                    ? "interactive second factor"
                    : string.Join(", ", twoFactor.Factors);
                throw new SampleBotInteractiveAuthRequiredException(
                    $"LocalPassword mode cannot complete interactive authentication. /api/auth/login requires {factors}. Use ExternalExchange or InternalToken instead.");
            }

            if (localPasswordMode && TryReadEnrollmentRequired(payload, out _))
            {
                throw new SampleBotInteractiveAuthRequiredException(
                    "LocalPassword mode cannot complete interactive authentication. /api/auth/login requires 2FA enrollment before issuing an internal trading JWT.");
            }

            throw new SampleBotAuthException($"{endpoint} returned an unexpected success payload.");
        }

        var error = TryReadError(payload, out var errorCode)
            ? errorCode
            : $"http_{(int)response.StatusCode}";
        throw new SampleBotAuthException($"{endpoint} failed: {error} (HTTP {(int)response.StatusCode}).");
    }

    internal static bool TryReadLoginResponse(JsonElement payload, out LoginResponse response)
    {
        response = default!;
        if (!payload.TryGetProperty("token", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("expiresAt", out var expiresAtElement)
            || expiresAtElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var token = tokenElement.GetString();
        var expiresAt = expiresAtElement.GetString();
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(expiresAt)
            || !DateTimeOffset.TryParse(expiresAt, out var parsedExpiresAt))
        {
            return false;
        }

        response = new LoginResponse(token, parsedExpiresAt);
        return true;
    }

    internal static bool TryReadTwoFactorRequired(JsonElement payload, out LoginTwoFactorRequiredResponse response)
    {
        response = default!;
        if (!payload.TryGetProperty("requires2fa", out var requiresElement)
            || requiresElement.ValueKind is not JsonValueKind.True)
        {
            return false;
        }

        var challengeToken = payload.TryGetProperty("challengeToken", out var challengeElement)
            ? challengeElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(challengeToken))
            return false;

        var factors = Array.Empty<string>();
        if (payload.TryGetProperty("factors", out var factorsElement) && factorsElement.ValueKind == JsonValueKind.Array)
        {
            factors = factorsElement
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        var totpChallengeToken = payload.TryGetProperty("totpChallengeToken", out var totpElement)
            && totpElement.ValueKind == JsonValueKind.String
            ? totpElement.GetString()
            : null;

        response = new LoginTwoFactorRequiredResponse(true, challengeToken, factors, totpChallengeToken);
        return true;
    }

    internal static bool TryReadEnrollmentRequired(JsonElement payload, out LoginEnrollmentRequiredResponse response)
    {
        response = default!;
        if (!payload.TryGetProperty("requires2faEnrollment", out var requiresElement)
            || requiresElement.ValueKind is not JsonValueKind.True)
        {
            return false;
        }

        var enrollmentToken = payload.TryGetProperty("enrollmentToken", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(enrollmentToken))
            return false;

        response = new LoginEnrollmentRequiredResponse(true, enrollmentToken);
        return true;
    }

    internal static bool TryReadError(JsonElement payload, out string error)
    {
        error = string.Empty;
        if (!payload.TryGetProperty("error", out var errorElement) || errorElement.ValueKind != JsonValueKind.String)
            return false;

        error = errorElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(error);
    }
}

internal sealed class AuthenticatedSessionCache
{
    private readonly ISampleBotAuthProvider _authProvider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AuthenticatedSession? _cached;

    public AuthenticatedSessionCache(ISampleBotAuthProvider authProvider, TimeProvider timeProvider)
    {
        _authProvider = authProvider;
        _timeProvider = timeProvider;
    }

    public async Task<AuthenticatedSession> GetAsync(CancellationToken cancellationToken)
    {
        if (IsFresh(_cached))
            return _cached!;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh(_cached))
                return _cached!;

            _cached = await _authProvider.AuthenticateAsync(cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    private bool IsFresh(AuthenticatedSession? session)
    {
        if (session is null)
            return false;

        if (session.ExpiresAt is null)
            return true;

        return session.ExpiresAt > _timeProvider.GetUtcNow().AddMinutes(1);
    }
}

public sealed record AuthenticatedSession(string Token, DateTimeOffset? ExpiresAt, SampleBotAuthMode SourceMode);

public class SampleBotAuthException : Exception
{
    public SampleBotAuthException(string message) : base(message)
    {
    }
}

public sealed class SampleBotInteractiveAuthRequiredException : SampleBotAuthException
{
    public SampleBotInteractiveAuthRequiredException(string message) : base(message)
    {
    }
}

internal sealed record LoginRequest(string Username, string Password);
internal sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
internal sealed record LoginTwoFactorRequiredResponse(bool Requires2fa, string ChallengeToken, IReadOnlyList<string> Factors, string? TotpChallengeToken);
internal sealed record LoginEnrollmentRequiredResponse(bool Requires2faEnrollment, string EnrollmentToken);
