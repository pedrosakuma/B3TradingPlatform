using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_UserBot;

[Trait("Category", "Conformance")]
public class FixpListenerSpecTests
{
    [ConformanceFact(RequiresFixpListener = true)]
    public async Task Negotiate_Establish_HappyPath_ReturnsWireAcks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var credential = await CreateCredentialAsync("wire-happy", cts.Token);
        await using var wire = await FixpWireClient.ConnectAsync(cts.Token);

        var negotiate = await wire.NegotiateAsync(
            credential.SessionId, credential.SessionVerId, credential.PlainSecret, cts.Token);
        Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, negotiate.TemplateId);

        var establish = await wire.EstablishAsync(
            credential.SessionId, credential.SessionVerId, cts.Token);
        Assert.Equal((ushort)EstablishAckData.MESSAGE_ID, establish.TemplateId);
    }

    [ConformanceFact(RequiresFixpListener = true)]
    public async Task Negotiate_BadCredentials_ReturnsWireReject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var wire = await FixpWireClient.ConnectAsync(cts.Token);

        var frame = await wire.NegotiateAsync(
            1, 1, "b3t_unknown_zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", cts.Token);

        Assert.Equal((ushort)NegotiateRejectData.MESSAGE_ID, frame.TemplateId);
        Assert.Equal(
            NegotiationRejectCode.CREDENTIALS,
            frame.Decode<NegotiateRejectData>().NegotiationRejectCode);
    }

    [ConformanceFact(RequiresFixpListener = true)]
    public async Task Establish_StaleVersion_ReturnsWireReject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var credential = await CreateCredentialAsync("wire-stale", cts.Token);
        var staleVersion = credential.SessionVerId + 99;
        await using var wire = await FixpWireClient.ConnectAsync(cts.Token);

        var negotiate = await wire.NegotiateAsync(
            credential.SessionId, staleVersion, credential.PlainSecret, cts.Token);
        Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, negotiate.TemplateId);

        var establish = await wire.EstablishAsync(credential.SessionId, staleVersion, cts.Token);
        Assert.Equal((ushort)EstablishRejectData.MESSAGE_ID, establish.TemplateId);
        Assert.Equal(
            EstablishRejectCode.INVALID_SESSIONVERID,
            establish.Decode<EstablishRejectData>().EstablishmentRejectCode);
    }

    [ConformanceFact(RequiresFixpListener = true)]
    public async Task RateLimit_BurstNegotiates_StartsWireRejecting()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var credential = await CreateCredentialAsync("wire-rate", cts.Token);
        var burst = PlatformEndpoint.GetFixpNegotiateBurst();

        for (var i = 0; i < burst; i++)
        {
            await using var admitted = await FixpWireClient.ConnectAsync(cts.Token);
            var response = await admitted.NegotiateAsync(
                credential.SessionId, credential.SessionVerId, credential.PlainSecret, cts.Token);
            Assert.Equal((ushort)NegotiateResponseData.MESSAGE_ID, response.TemplateId);
        }

        await using var rejected = await FixpWireClient.ConnectAsync(cts.Token);
        var reject = await rejected.NegotiateAsync(
            credential.SessionId, credential.SessionVerId, credential.PlainSecret, cts.Token);
        Assert.Equal((ushort)NegotiateRejectData.MESSAGE_ID, reject.TemplateId);
        Assert.Equal(
            NegotiationRejectCode.CREDENTIALS,
            reject.Decode<NegotiateRejectData>().NegotiationRejectCode);
    }

    private static async Task<CreatedCredential> CreateCredentialAsync(string label, CancellationToken ct)
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var auth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/user-bot-credentials/")
        {
            Content = JsonContent.Create(new { label = $"{label}-{Guid.NewGuid():N}" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(auth.Scheme, auth.Parameter);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"Credential create failed: {(int)response.StatusCode} {body}");
        var created = (await response.Content.ReadFromJsonAsync<CreatedSecret>(cancellationToken: ct))!;
        using var sessionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/user-bot-credentials/{created.Id}/session");
        sessionRequest.Headers.Authorization = auth;
        using var sessionResponse = await http.SendAsync(sessionRequest, ct);
        var sessionBody = await sessionResponse.Content.ReadAsStringAsync(ct);
        Assert.True(
            sessionResponse.IsSuccessStatusCode,
            $"Session allocation failed: {(int)sessionResponse.StatusCode} {sessionBody}");
        var session = (await sessionResponse.Content.ReadFromJsonAsync<FixpSession>(cancellationToken: ct))!;
        return new CreatedCredential(
            created.Id,
            created.PlainSecret,
            session.SessionId,
            session.SessionVerId);
    }

    private sealed record CreatedCredential(
        Guid Id,
        string PlainSecret,
        uint SessionId,
        ulong SessionVerId);

    private sealed record CreatedSecret(Guid Id, string PlainSecret);

    private sealed record FixpSession(uint SessionId, ulong SessionVerId);
}
