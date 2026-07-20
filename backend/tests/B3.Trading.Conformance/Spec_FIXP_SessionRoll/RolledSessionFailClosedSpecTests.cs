using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

[Trait("Category", "Conformance")]
public sealed class RolledSessionFailClosedSpecTests
{
    [ConformanceFact(
        RequiresAdmin = true,
        RequiresSandboxMatching = true,
        RequiresDockerControl = true)]
    public async Task RolledSession_AmbiguousMutationBlocksIngressUntilAuthoritativeResolution()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var user = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var maker = await LoginHelper.LoginAsync(
            http,
            peer.AdminUsername!,
            peer.AdminPassword!);
        var checkerUsername = Environment.GetEnvironmentVariable("B3T_CHECKER_USER")
            ?? throw new InvalidOperationException("B3T_CHECKER_USER is required.");
        var checkerPassword = Environment.GetEnvironmentVariable("B3T_CHECKER_PASS")
            ?? throw new InvalidOperationException("B3T_CHECKER_PASS is required.");
        var checker = await LoginHelper.LoginAsync(
            http,
            checkerUsername,
            checkerPassword);
        var docker = new DockerVenueTransportController();
        var runId = Guid.NewGuid().ToString("N");
        var before = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(
            http,
            maker);
        var baseline = (await GetMutationsAsync(http, maker))
            .Select(mutation => mutation.MutationId)
            .ToHashSet(StringComparer.Ordinal);
        var price = SessionRollSpecSupport.PriceNearLowerCollar(
            await SessionRollSpecSupport.GetEffectiveReferencePriceAsync(
                http,
                maker,
                "PETR4"));

        await using (var paused = await docker.PauseMatchingAsync())
        {
            var submissions = Enumerable.Range(0, 4)
                .Select(index => SubmitFaultWindowProbeAsync(
                    http,
                    user,
                    price,
                    runId,
                    index))
                .ToArray();
            await Task.WhenAll(submissions);
            _ = await WaitForNewAttemptedMutationsAsync(
                http,
                maker,
                baseline);
            var crashStartedUtc = DateTimeOffset.UtcNow;
            await docker.KillTradingHostAsync();
            await docker.WaitForTradingHostNotRunningAsync(
                TimeSpan.FromSeconds(10));
            await paused.RestartAsync(TimeSpan.FromSeconds(30));
            await docker.StartTradingHostAsync();
            await docker.WaitForTradingHostRestartAsync(
                crashStartedUtc,
                TimeSpan.FromSeconds(30));
        }

        _ = await SessionRollSpecSupport.WaitForFirmEstablishedAsync(
            http,
            maker,
            priorVerId: before.SessionVerId,
            expectAdvance: true);
        var unresolved = await WaitForNewUnresolvedMutationsAsync(
            http,
            maker,
            baseline);

        Assert.Contains(
            unresolved,
            mutation => string.Equals(
                mutation.State,
                "ambiguous",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await http.GetAsync("/ready")).StatusCode);
        using var blocked = await SubmitOrderAsync(
            http,
            user,
            price,
            $"{runId}-blocked-after-roll");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);

        foreach (var mutation in unresolved)
            await ResolveVenueAbsentAsync(http, maker, checker, mutation.MutationId);

        await WaitForReadyAsync(http);
        using var reopened = await SubmitOrderAsync(
            http,
            user,
            price,
            $"{runId}-reopened-after-authoritative-resolution");
        Assert.Equal(HttpStatusCode.Accepted, reopened.StatusCode);
        var reopenedPayload = await reopened.Content.ReadFromJsonAsync<JsonElement>();
        var reopenedClOrdId = ulong.Parse(
            reopenedPayload.GetProperty("clOrdId").GetString()!);
        await SessionRollSpecSupport.CancelOrderIfPresentAsync(
            http,
            user,
            reopenedClOrdId);
    }

    private static async Task SubmitFaultWindowProbeAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        decimal price,
        string runId,
        int index)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await SubmitOrderAsync(
                http,
                auth,
                price,
                $"{runId}-roll-fault-{index}",
                cts.Token);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or HttpRequestException)
        {
        }
    }

    private static async Task<HttpResponseMessage> SubmitOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        decimal price,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                symbol = "PETR4",
                side = "Buy",
                type = "Limit",
                quantity = SessionRollSpecSupport.RoundTripQuantity,
                price,
            }),
        };
        request.Headers.Authorization = auth;
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await http.SendAsync(request, cancellationToken);
    }

    private static async Task<IReadOnlyList<MutationSummary>>
        WaitForNewAttemptedMutationsAsync(
            HttpClient http,
            AuthenticationHeaderValue auth,
            IReadOnlySet<string> baseline)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        IReadOnlyList<MutationSummary> last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = (await GetMutationsAsync(http, auth))
                .Where(mutation => !baseline.Contains(mutation.MutationId))
                .ToArray();
            if (last.Any(mutation => mutation.State is
                    "frame_prepared" or "transport_write_completed" or "ambiguous"))
            {
                return last;
            }

            await Task.Delay(SessionRollSpecSupport.PollInterval);
        }

        Assert.Fail(
            "The real transport fault did not cross the SDK frame-prepared boundary. " +
            $"Last new rows: {JsonSerializer.Serialize(last)}");
        return [];
    }

    private static async Task<IReadOnlyList<MutationSummary>>
        WaitForNewUnresolvedMutationsAsync(
            HttpClient http,
            AuthenticationHeaderValue auth,
            IReadOnlySet<string> baseline)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        IReadOnlyList<MutationSummary> last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = (await GetMutationsAsync(http, auth))
                .Where(mutation =>
                    !baseline.Contains(mutation.MutationId)
                    && mutation.RequiresReconciliation)
                .ToArray();
            if (last.Any(mutation => string.Equals(
                    mutation.State,
                    "ambiguous",
                    StringComparison.OrdinalIgnoreCase)))
                return last;
            await Task.Delay(SessionRollSpecSupport.PollInterval);
        }

        Assert.Fail(
            "The real transport fault did not produce a frame-prepared ambiguous mutation. " +
            $"Last new unresolved rows: {JsonSerializer.Serialize(last)}");
        return [];
    }

    private static async Task<IReadOnlyList<MutationSummary>> GetMutationsAsync(
        HttpClient http,
        AuthenticationHeaderValue auth)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/outbound-mutations/");
        request.Headers.Authorization = auth;
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<MutationList>();
        return payload?.Mutations ?? [];
    }

    private static async Task ResolveVenueAbsentAsync(
        HttpClient http,
        AuthenticationHeaderValue maker,
        AuthenticationHeaderValue checker,
        string mutationId)
    {
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"roll-resolution:{mutationId}")))
            .ToLowerInvariant();
        var evidenceReference = $"official-extract:{digest}";
        var now = DateTimeOffset.UtcNow;
        using var evidenceRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/outbound-mutations/{mutationId}/evidence")
        {
            Content = JsonContent.Create(new
            {
                sourceType = "official_extract",
                evidenceReference,
                coverageStartUtc = now.AddHours(-1),
                coverageEndUtc = now.AddHours(1),
                attestationReference = $"attestation:{digest}",
            }),
        };
        evidenceRequest.Headers.Authorization = maker;
        using var evidenceResponse = await http.SendAsync(evidenceRequest);
        evidenceResponse.EnsureSuccessStatusCode();

        using var resolutionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/outbound-mutations/{mutationId}/resolve")
        {
            Content = JsonContent.Create(new
            {
                decision = "venue_absent",
                evidenceType = "official_extract",
                evidenceReference,
                reason = "official_extract_attested",
            }),
        };
        resolutionRequest.Headers.Authorization = maker;
        using var resolutionResponse = await http.SendAsync(resolutionRequest);
        var resolutionBody = await resolutionResponse.Content.ReadAsStringAsync();
        Assert.True(
            resolutionResponse.StatusCode is HttpStatusCode.OK
                or HttpStatusCode.Accepted,
            $"Resolution proposal failed for {mutationId}: {(int)resolutionResponse.StatusCode} {resolutionBody}");
        if (resolutionResponse.StatusCode == HttpStatusCode.OK)
            return;

        var proposalId = JsonDocument.Parse(resolutionBody)
            .RootElement
            .GetProperty("proposalId")
            .GetString();
        using var approvalRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/outbound-mutations/{mutationId}/resolve/{proposalId}/approve");
        approvalRequest.Headers.Authorization = checker;
        using var approvalResponse = await http.SendAsync(approvalRequest);
        approvalResponse.EnsureSuccessStatusCode();
    }

    private static async Task WaitForReadyAsync(HttpClient http)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        HttpStatusCode? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await http.GetAsync("/ready");
            last = response.StatusCode;
            if (last == HttpStatusCode.OK)
                return;
            await Task.Delay(SessionRollSpecSupport.PollInterval);
        }

        Assert.Fail($"Readiness did not reopen after authoritative resolution; last status={(int?)last}.");
    }

    private sealed record MutationList(IReadOnlyList<MutationSummary> Mutations);

    private sealed record MutationSummary(
        string MutationId,
        string State,
        bool RequiresReconciliation);
}
