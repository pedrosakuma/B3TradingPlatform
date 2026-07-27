using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_SessionRoll;

internal static class SessionRollSpecSupport
{
    internal const string FirmId = "FIRM01";
    internal const long RoundTripQuantity = 100;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan TradeTimeout = TimeSpan.FromSeconds(30);

    internal static async Task RunWithOrderCleanupAsync(
        HttpClient http,
        AuthenticationHeaderValue userAuth,
        AuthenticationHeaderValue adminAuth,
        Func<ulong?, ulong, Task<bool>> isVenueOrderPresent,
        Func<ulong?, ulong, Task> proveVenueOrderAbsent,
        Func<OrderCleanupScope, Task> scenario,
        Func<Task>? beforeOrderCleanup = null)
    {
        var cleanup = new OrderCleanupScope(
            http,
            userAuth,
            adminAuth,
            isVenueOrderPresent,
            proveVenueOrderAbsent);
        await cleanup.CaptureBaselineAsync();
        Exception? scenarioFailure = null;
        try
        {
            await scenario(cleanup);
        }
        catch (Exception ex)
        {
            scenarioFailure = ex;
        }

        var cleanupFailures = new List<Exception>();
        if (beforeOrderCleanup is not null)
        {
            try
            {
                await beforeOrderCleanup();
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Failed to restore the session-roll scenario before order cleanup.",
                    ex));
            }
        }

        cleanupFailures.AddRange(await cleanup.CleanupAsync());
        if (scenarioFailure is null)
        {
            if (cleanupFailures.Count > 0)
                throw new AggregateException("Session-roll order cleanup failed.", cleanupFailures);
            return;
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "The session-roll scenario failed and one or more tracked orders could not be terminalized.",
                new[] { scenarioFailure }.Concat(cleanupFailures));
        }

        ExceptionDispatchInfo.Capture(scenarioFailure).Throw();
    }

    internal static decimal PriceNearLowerCollar(decimal referencePrice)
        => decimal.Round(referencePrice * 0.92m, 2, MidpointRounding.AwayFromZero);

    internal static decimal PriceNearUpperCollar(decimal referencePrice)
        => decimal.Round(referencePrice * 1.08m, 2, MidpointRounding.AwayFromZero);

    private static async Task<ulong> SubmitOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol,
        decimal price,
        string side = "Buy",
        long quantity = RoundTripQuantity)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Headers = { Authorization = auth },
            Content = JsonContent.Create(new
            {
                symbol,
                side,
                type = "Limit",
                quantity,
                price,
            }),
        };

        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Accepted,
            $"POST /api/orders expected 202 Accepted, got {(int)resp.StatusCode}: {body}");

        var json = JsonDocument.Parse(body).RootElement;
        if (json.TryGetProperty("status", out var statusProp))
        {
            Assert.NotEqual("Rejected", statusProp.GetString());
        }

        return ulong.Parse(json.GetProperty("clOrdId").GetString()!);
    }

    internal static async Task AssertPostRecoveryTradingRoundTripAsync(
        OrderCleanupScope cleanup,
        HttpClient http,
        AuthenticationHeaderValue auth,
        DockerVenueTransportController docker,
        string symbol,
        decimal price,
        long quantity)
    {
        var submitStartUtc = DateTimeOffset.UtcNow;
        var buyClOrdId = await cleanup.SubmitOrderAsync(symbol, price, side: "Buy", quantity: quantity);
        await WaitForOrderAsync(http, auth, buyClOrdId, order =>
                (order.Status == "Working" && order.CumulativeQuantity == 0) ||
                (order.Status == "Filled" && order.CumulativeQuantity == quantity),
            OrderTimeout,
            "post-recovery buy order to reach Working (or immediately Filled against a surviving opposite book)");

        var sellClOrdId = await cleanup.SubmitOrderAsync(symbol, price, side: "Sell", quantity: quantity);

        // GET /api/orders is the full per-client history projection, not an
        // "open orders only" book view. Contract-level "disappears from the
        // book" therefore means the order leaves Working and reaches a
        // terminal state; it should remain queryable here as Filled.
        var filledBuy = await WaitForOrderAsync(http, auth, buyClOrdId, order =>
                order.Status == "Filled" && order.CumulativeQuantity == quantity,
            TradeTimeout,
            "post-recovery buy order to reach Filled");
        var filledSell = await WaitForOrderAsync(http, auth, sellClOrdId, order =>
                order.Status == "Filled" && order.CumulativeQuantity == quantity,
            TradeTimeout,
            "post-recovery sell order to reach Filled");

        Assert.Equal(quantity, filledBuy.CumulativeQuantity);
        Assert.Equal(quantity, filledSell.CumulativeQuantity);

        // The FIXP/order path can recover slightly ahead of the separate
        // UMDF channel-84 stream after a forced venue fault. Wait until
        // marketdata's own progress logs show the post-recovery trade window
        // drained without the reconnect-era stale gate still being on before
        // handing off to the next real-stack spec.
        await docker.WaitForMarketDataTradeDrainAsync(submitStartUtc, TradeTimeout);
    }

    internal static async Task StimulateGatewayWriteAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/orders/{clOrdId}");
        req.Headers.Authorization = auth;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using var response = await http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Intentional: the point is to force the host to attempt a FIXP
            // write while the venue leg is severed, not to assert on the
            // HTTP outcome of this probe cancel.
        }
        catch (HttpRequestException)
        {
        }
    }

    internal static async Task<OrderSnapshot> WaitForOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId,
        Func<OrderSnapshot, bool> predicate,
        TimeSpan timeout,
        string expectation)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        OrderSnapshot? last = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await TryGetOrderAsync(http, auth, clOrdId);
                lastError = null;
                if (last is not null && predicate(last))
                    return last;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for {expectation} on order {clOrdId}. Last observed={Format(last)} httpError={lastError ?? "<none>"}");
        return null!;
    }

    internal static async Task<OrderSnapshot?> TryGetOrderAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        ulong clOrdId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var orders = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
        if (orders is null)
            return null;

        foreach (var order in orders)
        {
            if (order.GetProperty("clOrdId").GetString() == clOrdId.ToString())
            {
                return new OrderSnapshot(
                    Status: order.GetProperty("status").GetString()!,
                    CumulativeQuantity: order.GetProperty("cumulativeQuantity").GetInt64(),
                    IsStale: order.TryGetProperty("isStale", out var staleProp) && staleProp.GetBoolean(),
                    StaleReason: order.TryGetProperty("staleReason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String
                        ? reasonProp.GetString()
                        : null);
            }
        }

        return null;
    }

    internal static async Task<FirmSnapshot> WaitForFirmEstablishedAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        uint? priorVerId = null,
        bool? expectAdvance = null)
    {
        var deadline = DateTimeOffset.UtcNow + ReconnectTimeout;
        FirmSnapshot? last = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await GetFirmSnapshotAsync(http, auth);
                lastError = null;
                var established = string.Equals(last.SessionState, "established", StringComparison.OrdinalIgnoreCase)
                                  && !last.Reconnecting;
                var verIdOkay = expectAdvance switch
                {
                    true => priorVerId.HasValue && last.SessionVerId > priorVerId.Value,
                    false => priorVerId.HasValue && last.SessionVerId == priorVerId.Value,
                    null => true,
                };

                if (established && verIdOkay)
                    return last;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail(
            $"Timed out after {ReconnectTimeout.TotalSeconds:F0}s waiting for {FirmId} sessionState=established and sessionVerId expectation {DescribeVerIdExpectation(priorVerId, expectAdvance)}. Last observed={Format(last)} httpError={lastError ?? "<none>"}");
        return null!;
    }

    internal static async Task<FirmSnapshot> GetFirmSnapshotAsync(
        HttpClient http,
        AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/firms");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var firms = json.GetProperty("firms");
        foreach (var firm in firms.EnumerateArray())
        {
            if (firm.GetProperty("firmId").GetString() == FirmId)
            {
                return new FirmSnapshot(
                    SessionState: firm.TryGetProperty("sessionState", out var stateProp) && stateProp.ValueKind == JsonValueKind.String
                        ? stateProp.GetString()
                        : null,
                    SessionVerId: GetUInt32Flexible(firm.GetProperty("sessionVerId")),
                    Reconnecting: firm.TryGetProperty("reconnecting", out var reconnectingProp)
                                  && reconnectingProp.ValueKind == JsonValueKind.True);
            }
        }

        Assert.Fail($"Firm '{FirmId}' not found in /api/admin/firms response.");
        return null!;
    }

    internal static async Task<decimal> GetEffectiveReferencePriceAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string symbol)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/api/admin/marketdata/reference-prices?symbols={Uri.EscapeDataString(symbol)}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entry = json.GetProperty("symbols")[0];

        if (entry.TryGetProperty("effectivePrice", out var effectiveProp) &&
            effectiveProp.ValueKind == JsonValueKind.Number)
        {
            return effectiveProp.GetDecimal();
        }

        if (entry.TryGetProperty("fallbackPrice", out var fallbackProp) &&
            fallbackProp.ValueKind == JsonValueKind.Number)
        {
            return fallbackProp.GetDecimal();
        }

        Assert.Fail($"No effective/fallback reference price available for {symbol}.");
        return 0m;
    }

    internal static async Task DelayUntilAsync(DateTimeOffset startedUtc, TimeSpan targetDuration)
    {
        var remaining = startedUtc + targetDuration - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
    }

    private static uint GetUInt32Flexible(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetUInt32(),
        JsonValueKind.String => uint.Parse(value.GetString()!),
        _ => throw new InvalidOperationException($"Expected uint-compatible sessionVerId, observed {value.ValueKind}."),
    };

    private static string DescribeVerIdExpectation(uint? priorVerId, bool? expectAdvance) => expectAdvance switch
    {
        true => $">{priorVerId}",
        false => $"=={priorVerId}",
        null => "<any>",
    };

    private static string Format(OrderSnapshot? order) => order is null
        ? "<missing>"
        : $"{{ status={order.Status}, cumulativeQuantity={order.CumulativeQuantity}, isStale={order.IsStale}, staleReason={order.StaleReason ?? "null"} }}";

    private static string Format(FirmSnapshot? firm) => firm is null
        ? "<missing>"
        : $"{{ sessionState={firm.SessionState ?? "null"}, sessionVerId={firm.SessionVerId}, reconnecting={firm.Reconnecting} }}";

    internal sealed class OrderCleanupScope(
        HttpClient http,
        AuthenticationHeaderValue userAuth,
        AuthenticationHeaderValue adminAuth,
        Func<ulong?, ulong, Task<bool>> isVenueOrderPresent,
        Func<ulong?, ulong, Task> proveVenueOrderAbsent)
    {
        private readonly Dictionary<ulong, TrackedOrder> _orders = [];
        private readonly HashSet<ulong> _baselineOrderIds = [];

        internal async Task CaptureBaselineAsync()
        {
            foreach (var order in await GetOrdersAsync())
                _baselineOrderIds.Add(order.ClOrdId);
        }

        internal async Task<ulong> SubmitOrderAsync(
            string symbol,
            decimal price,
            string side = "Buy",
            long quantity = RoundTripQuantity)
        {
            var clOrdId = await SessionRollSpecSupport.SubmitOrderAsync(
                http,
                userAuth,
                symbol,
                price,
                side,
                quantity);
            TrackOrder(clOrdId, symbol, side, price, quantity);
            return clOrdId;
        }

        internal void TrackOrder(
            ulong clOrdId,
            string symbol,
            string side,
            decimal price,
            long quantity)
        {
            _orders.TryAdd(clOrdId, new TrackedOrder(clOrdId, symbol, side, price, quantity));
        }

        internal async Task<IReadOnlyList<Exception>> CleanupAsync()
        {
            var failures = new List<Exception>();
            try
            {
                foreach (var order in await GetOrdersAsync())
                {
                    if (!_baselineOrderIds.Contains(order.ClOrdId))
                        _orders.TryAdd(order.ClOrdId, order);
                }
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Failed to discover unregistered orders created during the session-roll scenario.",
                    ex));
            }

            foreach (var order in _orders.Values.ToArray())
            {
                try
                {
                    await TerminalizeAsync(order);
                }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException(
                        $"Failed to terminalize tracked {order.Side} order {order.ClOrdId} " +
                        $"{order.Symbol} {order.Quantity}@{order.Price}.",
                        ex));
                }
            }

            return failures;
        }

        private async Task<IReadOnlyList<TrackedOrder>> GetOrdersAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
            request.Headers.Authorization = userAuth;
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var orders = await response.Content.ReadFromJsonAsync<JsonElement[]>();
            if (orders is null)
                return [];

            return orders
                .Select(static order => new TrackedOrder(
                    ClOrdId: ulong.Parse(order.GetProperty("clOrdId").GetString()!),
                    Symbol: order.GetProperty("symbol").GetString()!,
                    Side: order.GetProperty("side").GetString()!,
                    Price: order.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Number
                        ? price.GetDecimal()
                        : null,
                    Quantity: order.GetProperty("quantity").GetInt64()))
                .ToArray();
        }

        private async Task TerminalizeAsync(TrackedOrder order)
        {
            var deadline = DateTimeOffset.UtcNow + TradeTimeout;
            var acknowledgementDeadline =
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var before = await TryGetOrderAsync(http, userAuth, order.ClOrdId)
                    ?? throw new InvalidOperationException(
                        $"Tracked order {order.ClOrdId} is absent from the client order history; venue terminality cannot be proven.");
                if (IsTerminal(before))
                    return;

                var proof = await GetVenueOrderProofAsync(order.ClOrdId);
                if (proof.VenueAbsent)
                {
                    await MarkVenueAbsentAsync(order.ClOrdId, before);
                    return;
                }
                if (proof.ActiveCancelMutationId is { } activeCancelMutationId)
                {
                    if (!await isVenueOrderPresent(
                            proof.VenueOrderId,
                            order.ClOrdId))
                    {
                        throw new InvalidOperationException(
                            $"Tracked order {order.ClOrdId} is already absent from matching, " +
                            $"but active cancel mutation {activeCancelMutationId} cannot be " +
                            "truthfully resolved VenueAbsent.");
                    }

                    await ResolveCancelMutationVenueAbsentAsync(
                        activeCancelMutationId);
                    continue;
                }
                if (proof.VenueOrderId is not { } venueOrderId)
                {
                    if (proof.AwaitingVenueAcknowledgement)
                    {
                        if (DateTimeOffset.UtcNow >= acknowledgementDeadline)
                        {
                            await proveVenueOrderAbsent(null, order.ClOrdId);
                            return;
                        }
                        await Task.Delay(PollInterval);
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"Tracked order {order.ClOrdId} has no acknowledged venue OrderId " +
                        "and no authoritative VenueAbsent resolution.");
                }

                if (before.IsStale)
                {
                    await ClearStaleAsync(order.ClOrdId);
                    before = await TryGetOrderAsync(http, userAuth, order.ClOrdId)
                        ?? throw new InvalidOperationException(
                            $"Tracked order {order.ClOrdId} disappeared locally after clearing stale.");
                    if (IsTerminal(before))
                        return;
                    if (before.IsStale)
                    {
                        throw new InvalidOperationException(
                            $"Tracked order {order.ClOrdId} remained stale after the admin clear.");
                    }
                }

                using var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/api/orders/{order.ClOrdId}");
                cancel.Headers.Authorization = userAuth;
                using var response = await http.SendAsync(cancel);
                var body = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero &&
                        await WaitForTerminalAsync(
                            order.ClOrdId,
                            Min(remaining, TimeSpan.FromSeconds(2))))
                    {
                        return;
                    }

                    await proveVenueOrderAbsent(venueOrderId, order.ClOrdId);
                    var venueAbsentLast = await TryGetOrderAsync(http, userAuth, order.ClOrdId);
                    if (venueAbsentLast is not null && !IsTerminal(venueAbsentLast))
                        await MarkVenueAbsentAsync(order.ClOrdId, venueAbsentLast);
                    return;
                }

                var after = await TryGetOrderAsync(http, userAuth, order.ClOrdId);
                if (after is not null && IsTerminal(after))
                    return;

                if (response.StatusCode == HttpStatusCode.Conflict &&
                    after?.IsStale == true)
                {
                    await ClearStaleAsync(order.ClOrdId);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                {
                    await Task.Delay(PollInterval);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        $"DELETE /api/orders/{order.ClOrdId} returned 404 while local state remained nonterminal; " +
                        $"venue absence cannot be proven. Last observed={Format(after)}.");
                }

                throw new InvalidOperationException(
                    $"DELETE /api/orders/{order.ClOrdId} did not establish terminality: " +
                    $"{(int)response.StatusCode} {body}; last observed={Format(after)}.");
            }

            var last = await TryGetOrderAsync(http, userAuth, order.ClOrdId);
            throw new TimeoutException(
                $"Timed out after {TradeTimeout.TotalSeconds:F0}s terminalizing tracked order {order.ClOrdId}. " +
                $"Last observed={Format(last)}.");
        }

        private async Task<VenueOrderProof> GetVenueOrderProofAsync(ulong clOrdId)
        {
            using var listRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/admin/outbound-mutations/");
            listRequest.Headers.Authorization = adminAuth;
            using var listResponse = await http.SendAsync(listRequest);
            listResponse.EnsureSuccessStatusCode();
            using var listDocument = JsonDocument.Parse(
                await listResponse.Content.ReadAsStringAsync());
            var mutations = listDocument.RootElement.GetProperty("mutations");
            string? mutationId = null;
            string? mutationState = null;
            var requiresReconciliation = false;
            string? activeCancelMutationId = null;
            foreach (var mutation in mutations.EnumerateArray())
            {
                var kind = mutation.GetProperty("kind").GetString();
                if (kind == "cancel" &&
                    mutation.TryGetProperty("originalClOrdId", out var original) &&
                    original.ValueKind is not JsonValueKind.Null &&
                    TryReadUInt64(original, out var originalClOrdId) &&
                    originalClOrdId == clOrdId &&
                    mutation.TryGetProperty("requiresReconciliation", out var cancelRequires) &&
                    cancelRequires.ValueKind == JsonValueKind.True)
                {
                    activeCancelMutationId =
                        mutation.GetProperty("mutationId").GetString();
                    continue;
                }
                if (kind is not ("new" or "replace") ||
                    !TryReadUInt64(mutation.GetProperty("primaryClOrdId"), out var primaryClOrdId) ||
                    primaryClOrdId != clOrdId)
                {
                    continue;
                }

                mutationId = mutation.GetProperty("mutationId").GetString();
                mutationState = mutation.GetProperty("state").GetString();
                requiresReconciliation =
                    mutation.TryGetProperty("requiresReconciliation", out var requires) &&
                    requires.ValueKind == JsonValueKind.True;
            }

            if (string.IsNullOrWhiteSpace(mutationId))
            {
                throw new InvalidOperationException(
                    $"No new/replace outbound mutation was found for tracked order {clOrdId}.");
            }

            using var detailRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/admin/outbound-mutations/{mutationId}");
            detailRequest.Headers.Authorization = adminAuth;
            using var detailResponse = await http.SendAsync(detailRequest);
            detailResponse.EnsureSuccessStatusCode();
            using var detailDocument = JsonDocument.Parse(
                await detailResponse.Content.ReadAsStringAsync());
            var root = detailDocument.RootElement;
            if (root.TryGetProperty("resolution", out var resolution) &&
                resolution.ValueKind == JsonValueKind.Object &&
                resolution.TryGetProperty("venueOrderId", out var venueOrderId) &&
                TryReadUInt64(venueOrderId, out var parsedVenueOrderId))
            {
                return new VenueOrderProof(
                    parsedVenueOrderId,
                    VenueAbsent: false,
                    ActiveCancelMutationId: activeCancelMutationId);
            }

            if (root.TryGetProperty("operatorEvidence", out var operatorEvidence) &&
                operatorEvidence.ValueKind == JsonValueKind.Array &&
                mutationState == "operator_resolved" &&
                !requiresReconciliation &&
                operatorEvidence.EnumerateArray().Any(evidence =>
                    evidence.TryGetProperty("decision", out var decision) &&
                    decision.GetString() == "venue_absent"))
            {
                return new VenueOrderProof(
                    null,
                    VenueAbsent: true,
                    AwaitingVenueAcknowledgement: false,
                    ActiveCancelMutationId: activeCancelMutationId);
            }

            if (mutationState == "proven_unsent")
            {
                return new VenueOrderProof(
                    null,
                    VenueAbsent: true,
                    AwaitingVenueAcknowledgement: false,
                    ActiveCancelMutationId: activeCancelMutationId);
            }

            return new VenueOrderProof(
                null,
                VenueAbsent: false,
                AwaitingVenueAcknowledgement:
                    !requiresReconciliation &&
                    mutationState is
                        "approved_to_send" or
                        "attempt_intent_prepared" or
                        "frame_prepared" or
                        "transport_write_completed",
                ActiveCancelMutationId: activeCancelMutationId);
        }

        private async Task ResolveCancelMutationVenueAbsentAsync(
            string mutationId)
        {
            var digest = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(
                            $"cleanup-cancel-resolution:{mutationId}")))
                .ToLowerInvariant();
            var evidenceReference = $"official-extract:{digest}";
            var now = DateTimeOffset.UtcNow;
            using var evidenceRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/admin/outbound-mutations/{mutationId}/evidence")
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
            evidenceRequest.Headers.Authorization = adminAuth;
            using var evidenceResponse = await http.SendAsync(evidenceRequest);
            evidenceResponse.EnsureSuccessStatusCode();

            using var resolutionRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/admin/outbound-mutations/{mutationId}/resolve")
            {
                Content = JsonContent.Create(new
                {
                    decision = "venue_absent",
                    evidenceType = "official_extract",
                    evidenceReference,
                    reason = "official_extract_attested",
                }),
            };
            resolutionRequest.Headers.Authorization = adminAuth;
            using var resolutionResponse = await http.SendAsync(resolutionRequest);
            var body = await resolutionResponse.Content.ReadAsStringAsync();
            if (resolutionResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Cancel mutation {mutationId} VenueAbsent resolution expected 200, got " +
                    $"{(int)resolutionResponse.StatusCode}: {body}");
            }
        }

        private async Task ClearStaleAsync(ulong clOrdId)
        {
            using var clear = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/admin/firms/{FirmId}/orders/{clOrdId}/clear-stale");
            clear.Headers.Authorization = adminAuth;
            using var response = await http.SendAsync(clear);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new InvalidOperationException(
                    $"POST clear-stale for tracked order {clOrdId} expected 204, got " +
                    $"{(int)response.StatusCode}: {body}");
            }
        }

        private async Task MarkVenueAbsentAsync(
            ulong clOrdId,
            OrderSnapshot order)
        {
            if (order.IsStale ||
                order.Status is not ("Working" or "PartiallyFilled"))
            {
                return;
            }

            using var mark = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/admin/firms/{FirmId}/orders/{clOrdId}/mark-stale")
            {
                Content = JsonContent.Create(new
                {
                    reason = "cleanup_venue_absent",
                }),
            };
            mark.Headers.Authorization = adminAuth;
            using var response = await http.SendAsync(mark);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new InvalidOperationException(
                    $"POST mark-stale for venue-absent order {clOrdId} expected 204, got " +
                    $"{(int)response.StatusCode}: {body}");
            }
        }

        private async Task<bool> WaitForTerminalAsync(ulong clOrdId, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var order = await TryGetOrderAsync(http, userAuth, clOrdId);
                    if (order is not null && IsTerminal(order))
                        return true;
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(PollInterval);
            }

            return false;
        }

        private static bool IsTerminal(OrderSnapshot order) =>
            order.Status is "Filled" or "Cancelled" or "Rejected" or "Replaced";

        private static bool TryReadUInt64(JsonElement value, out ulong parsed)
        {
            if (value.ValueKind == JsonValueKind.Number)
                return value.TryGetUInt64(out parsed);
            if (value.ValueKind == JsonValueKind.String)
                return ulong.TryParse(value.GetString(), out parsed);
            parsed = 0;
            return false;
        }

        private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
            left <= right ? left : right;
    }

    internal sealed record OrderSnapshot(
        string Status,
        long CumulativeQuantity,
        bool IsStale,
        string? StaleReason);

    internal sealed record FirmSnapshot(
        string? SessionState,
        uint SessionVerId,
        bool Reconnecting);

    private sealed record TrackedOrder(
        ulong ClOrdId,
        string Symbol,
        string Side,
        decimal? Price,
        long Quantity);

    private sealed record VenueOrderProof(
        ulong? VenueOrderId,
        bool VenueAbsent,
        bool AwaitingVenueAcknowledgement = false,
        string? ActiveCancelMutationId = null);

}
