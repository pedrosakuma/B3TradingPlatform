using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace B3.Trading.Conformance.Infrastructure;

/// <summary>
/// Q1.7 (#259). Helper used by the (OrderType × TimeInForce) golden
/// snapshot scenarios. Encapsulates the four operations every scenario
/// repeats verbatim: login (user + optional admin), submit a NewOrder via
/// <c>POST /api/orders/</c>, drive synthetic ER sequences via
/// <c>POST /api/admin/simulator/er</c>, and capture the WS
/// <c>executions.me</c> stream for the order under test, normalising the
/// captured ERs into a deterministic JSON shape suitable for byte-for-byte
/// comparison against a checked-in golden.
///
/// <para>Normalisation rules (per #259 spec): drop volatile fields
/// (<c>timestampUtc</c>, any UUID-shaped id, <c>orderId</c>, <c>tradeId</c>);
/// keep contract-relevant fields (<c>side</c>, <c>type</c>, <c>tif</c>,
/// <c>price</c>, <c>stopPrice</c>, <c>orderQty</c>, <c>cumQty</c>,
/// <c>leavesQty</c>, <c>execType</c>, <c>ordStatus</c>, <c>execKind</c>);
/// sort object keys alphabetically; preserve emission order of the ER
/// sequence (the contract's whole point — out-of-order ERs would be a
/// bug).</para>
/// </summary>
public sealed class ConformanceRunner : IAsyncDisposable
{
    /// <summary>
    /// Pass-1 review fix: after <see cref="CaptureExecutionsWhileAsync"/>
    /// reaches <c>expectedCount</c> ERs, keep the WS open for this long
    /// to make sure no extra ER for the same order arrives. A forbidden
    /// extra ER fails the call with a clear message instead of being
    /// silently dropped (which would let "expected 2, host actually
    /// emitted 3" regressions slip through). Override per-process via
    /// the <c>B3T_CONFORMANCE_QUIESCENCE_MS</c> env var.
    /// </summary>
    public static int QuiescenceWindowMs { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("B3T_CONFORMANCE_QUIESCENCE_MS"), out var v) && v >= 0
            ? v
            : 250;

    private static readonly JsonSerializerOptions GoldenJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly HashSet<string> KeptErFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "clOrdId",
        "symbol",
        "side",
        "status",
        "kind",
        "leavesQuantity",
        "cumulativeQuantity",
        "lastQuantity",
        "lastPrice",
        "rejectReason",
        "isNativeStp",
    };

    public PlatformEndpoint Peer { get; }
    public HttpClient Http { get; }
    public AuthenticationHeaderValue UserAuth { get; private set; } = null!;
    public AuthenticationHeaderValue? AdminAuth { get; private set; }

    private string? _userBearerToken;

    private ConformanceRunner(PlatformEndpoint peer)
    {
        Peer = peer;
        Http = new HttpClient { BaseAddress = peer.BaseUrl };
    }

    /// <summary>
    /// Log in as the user (always) and the admin (when peer has admin
    /// credentials configured). The admin login is needed by every scenario
    /// that drives ER injection.
    /// </summary>
    public static async Task<ConformanceRunner> CreateAsync(PlatformEndpoint peer, bool requireAdmin = true)
    {
        var runner = new ConformanceRunner(peer);
        var (header, token) = await LoginCapturingTokenAsync(runner.Http, peer.Username, peer.Password);
        runner.UserAuth = header;
        runner._userBearerToken = token;
        if (requireAdmin && peer.HasAdminCredentials)
        {
            runner.AdminAuth = await LoginHelper.LoginAsync(runner.Http, peer.AdminUsername!, peer.AdminPassword!);
        }
        return runner;
    }

    private static async Task<(AuthenticationHeaderValue Header, string Token)> LoginCapturingTokenAsync(
        HttpClient http, string username, string password)
    {
        var resp = await http.PostAsJsonAsync("/api/auth/login", new { username, password });
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"login failed for '{username}': {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        var payload = await resp.Content.ReadFromJsonAsync<LoginResponse>()
                      ?? throw new InvalidOperationException("login response empty");
        return (new AuthenticationHeaderValue("Bearer", payload.Token), payload.Token);
    }

    private sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

    /// <summary>POST /api/orders/ as the user; returns the assigned ClOrdID.</summary>
    public async Task<ulong> SubmitOrderAsync(object payload, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders/")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = UserAuth;
        var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException(
                $"submit failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        }
        var ack = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return ulong.Parse(ack.GetProperty("clOrdId").GetString()!);
    }

    /// <summary>POST /api/admin/simulator/er — see <see cref="Infrastructure"/> SimulatorEndpoint.</summary>
    public async Task InjectErAsync(
        ulong clOrdId, string type,
        long? lastQty = null, decimal? lastPx = null, string? rejectReason = null,
        CancellationToken ct = default)
    {
        if (AdminAuth is null)
            throw new InvalidOperationException("ER injection requires admin credentials.");

        object body = (lastQty, lastPx, rejectReason) switch
        {
            (long q, decimal p, null) => new { ClOrdId = clOrdId, Type = type, LastQty = q, LastPx = p },
            (long q, null, null) => new { ClOrdId = clOrdId, Type = type, LastQty = q },
            (null, null, string r) => new { ClOrdId = clOrdId, Type = type, RejectReason = r },
            _ => new { ClOrdId = clOrdId, Type = type },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/simulator/er")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = AdminAuth;
        var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException(
                $"er injection failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        }
    }

    /// <summary>GET /api/orders/{clOrdId} via the listing endpoint (no by-id route exists).</summary>
    public async Task<JsonElement?> GetOrderAsync(ulong clOrdId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders/");
        req.Headers.Authorization = UserAuth;
        var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var orders = await resp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct);
        if (orders is null) return null;
        foreach (var o in orders)
        {
            if (o.GetProperty("clOrdId").GetString() == clOrdId.ToString())
                return o;
        }
        return null;
    }

    /// <summary>
    /// Pass-2 review fix (#259, P1): subscribe-first capture.
    /// Open <c>/ws</c> with a bearer (via <c>?access_token=</c>),
    /// subscribe to <c>executions.me</c>, AWAIT the initial
    /// <c>{type:"snapshot",channel:"executions.me"}</c> frame from the
    /// server (proof the subscription is live in
    /// <see cref="SubscriptionManager"/>), then invoke
    /// <paramref name="driveErs"/> — typically the test's
    /// <see cref="InjectErAsync"/> calls — and accumulate ER frames
    /// whose <c>clOrdId</c> matches the supplied id until either
    /// <paramref name="expectedCount"/> ERs have arrived or
    /// <paramref name="timeout"/> elapses. Returns the captured ERs in
    /// arrival order.
    ///
    /// <para>Why subscribe-before-inject: <c>executions.me</c> has NO
    /// historical replay (see <c>SubscriptionManager.SubscribeWithSnapshot</c>:
    /// the snapshot for that channel is the empty array). If the test
    /// injected ERs before the WS fan-out actually saw the
    /// subscription, those ERs would be dropped on the floor and the
    /// capture would time out (or — worse, in CI on a fast box — would
    /// succeed by accident on most runs and flake intermittently).</para>
    /// </summary>
    public async Task<List<JsonObject>> CaptureExecutionsWhileAsync(
        ulong clOrdId, int expectedCount, TimeSpan timeout,
        Func<Task> driveErs, CancellationToken ct = default)
    {
        if (_userBearerToken is null)
            throw new InvalidOperationException("must be logged in before capturing executions.");

        using var ws = new ClientWebSocket();
        var wsUri = BuildWsUri(Peer.BaseUrl, _userBearerToken);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(wsUri, connectCts.Token);

        var subscribe = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "subscribe",
            channels = new[] { "executions.me" },
        });
        await ws.SendAsync(subscribe, WebSocketMessageType.Text, endOfMessage: true, ct);

        // Wait for the server's snapshot frame on executions.me as the
        // subscribe ack. SubscriptionManager.SubscribeWithSnapshot
        // enqueues a {type:"snapshot",channel:"executions.me",data:[]}
        // frame atomically with the subscription registration, so once
        // we have read it we KNOW any subsequent Publish() to this
        // owner will fan out to us.
        var buf = new byte[8 * 1024];
        var ackDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < ackDeadline)
        {
            using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ackCts.CancelAfter(ackDeadline - DateTime.UtcNow);
            var ackFrame = await ReadFrameAsync(ws, buf, ackCts.Token);
            if (ackFrame is null)
                throw new Xunit.Sdk.XunitException(
                    "WS closed before executions.me subscribe-ack snapshot arrived.");
            var t = ackFrame["type"]?.GetValue<string>();
            var c = ackFrame["channel"]?.GetValue<string>();
            if (string.Equals(t, "snapshot", StringComparison.Ordinal)
                && string.Equals(c, "executions.me", StringComparison.Ordinal))
            {
                break;
            }
            if (string.Equals(t, "error", StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException(
                    $"WS subscribe to executions.me returned error: {ackFrame.ToJsonString()}");
            }
            // Any other frame (e.g. a stale public-channel snapshot) —
            // keep reading until the executions.me snapshot lands.
        }
        if (DateTime.UtcNow >= ackDeadline)
        {
            throw new Xunit.Sdk.XunitException(
                "Timed out (10s) awaiting executions.me subscribe-ack snapshot.");
        }

        // Drive the ERs ONLY after we have proof of subscription.
        await driveErs();

        var captured = new List<JsonObject>();
        var deadline = DateTime.UtcNow + timeout;
        var closed = false;
        DateTime? quiescenceDeadline = null;
        try
        {
            while (!closed && DateTime.UtcNow < deadline)
            {
                // Pass-1 review fix: once we have the expected count,
                // switch into a fixed-duration "quiescence" window. Any
                // additional ER for this order during the window is a
                // contract violation; the WS receive timeout exits the
                // loop normally if nothing arrives.
                if (captured.Count >= expectedCount && quiescenceDeadline is null)
                {
                    quiescenceDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(QuiescenceWindowMs);
                }

                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                TimeSpan remaining;
                if (quiescenceDeadline is { } qd)
                {
                    remaining = qd - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                }
                else
                {
                    remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                }
                recvCts.CancelAfter(remaining);

                JsonObject? frame;
                try
                {
                    frame = await ReadFrameAsync(ws, buf, recvCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Receive timed out (no frames in window). For the
                    // pre-expectedCount path, this is a hard timeout and
                    // we exit. For the quiescence path, this is the
                    // success signal — no extra ER arrived.
                    break;
                }
                if (frame is null) { closed = true; break; }

                // Outbound envelope shape: {type,channel,seq,data,...}
                var channel = frame["channel"]?.GetValue<string>();
                if (!string.Equals(channel, "executions.me", StringComparison.Ordinal)) continue;

                // Skip the ack snapshot's empty-data echo if the server
                // emits any further snapshot frames (defensive).
                if (frame["data"] is not JsonObject erData) continue;
                var erClOrdId = erData["clOrdId"]?.GetValue<string>();
                if (erClOrdId != clOrdId.ToString()) continue;

                captured.Add(erData);

                // If we already had the expected count and another ER
                // for this order arrived within the quiescence window,
                // fail loudly with the offending payload — better than
                // a downstream golden mismatch.
                if (quiescenceDeadline is not null && captured.Count > expectedCount)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Forbidden extra ER for clOrdId={clOrdId} arrived inside the {QuiescenceWindowMs}ms " +
                        $"quiescence window after the expected {expectedCount} ERs were captured. " +
                        $"Offending payload: {erData.ToJsonString()}");
                }
            }
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch (WebSocketException) { /* best-effort */ }
        }

        // Final assertion: the captured ER count must EXACTLY equal the
        // expected count — neither a short-fall (timeout before all
        // expected ERs arrived) nor an over-shoot (caught above) is
        // acceptable.
        if (captured.Count != expectedCount)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected exactly {expectedCount} ERs for clOrdId={clOrdId}, captured {captured.Count} " +
                $"within {timeout.TotalSeconds:F1}s + {QuiescenceWindowMs}ms quiescence.");
        }
        return captured;
    }

    private static async Task<JsonObject?> ReadFrameAsync(
        ClientWebSocket ws, byte[] buf, CancellationToken ct)
    {
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
        } while (!result.EndOfMessage);
        return JsonNode.Parse(sb.ToString())?.AsObject();
    }

    private static Uri BuildWsUri(Uri baseUrl, string token)
    {
        var scheme = baseUrl.Scheme switch
        {
            "https" => "wss",
            _ => "ws",
        };
        var b = new UriBuilder(baseUrl)
        {
            Scheme = scheme,
            Path = "/ws",
            Query = "access_token=" + Uri.EscapeDataString(token),
        };
        return b.Uri;
    }

    /// <summary>
    /// Strip volatile fields (timestampUtc, etc) and produce a stable
    /// JSON document with sorted keys. Per #259 we ALSO strip the
    /// numeric clOrdId (it is monotonically allocated by the host and
    /// would diverge between runs) — what the golden asserts is the
    /// shape of the ER sequence, not the specific id.
    /// </summary>
    public string Normalize(IReadOnlyList<JsonObject> ers, IDictionary<string, JsonNode?>? extraContext = null)
    {
        var arr = new JsonArray();
        foreach (var er in ers)
        {
            var sorted = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (key, value) in er)
            {
                if (!KeptErFields.Contains(key)) continue;
                // Drop the host-allocated numeric clOrdId from the
                // captured frame: it's deterministic within a run but
                // diverges between runs (allocator counter starts at
                // boot). The scenario already pinned identity by
                // matching against the submitted clOrdId during capture.
                if (string.Equals(key, "clOrdId", StringComparison.OrdinalIgnoreCase)) continue;
                sorted[CamelCase(key)] = value?.DeepClone();
            }
            arr.Add(new JsonObject(sorted!));
        }

        var root = new JsonObject
        {
            ["scenario"] = new JsonObject(),
            ["executionReports"] = arr,
        };
        if (extraContext is not null)
        {
            var ctxObj = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (k, v) in extraContext) ctxObj[k] = v?.DeepClone();
            root["scenario"] = new JsonObject(ctxObj!);
        }
        return JsonSerializer.Serialize(root, GoldenJsonOptions);
    }

    private static string CamelCase(string s) =>
        string.IsNullOrEmpty(s) || char.IsLower(s[0]) ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>
    /// Loads the named golden from the <c>Goldens/</c> folder beside the
    /// test assembly, compares to <paramref name="actualJson"/> with
    /// trimmed whitespace and platform-agnostic line endings, and throws
    /// with a unified diff-ish message on mismatch.
    /// </summary>
    public static void AssertGoldenMatches(string actualJson, string goldenName)
    {
        var path = ResolveGoldenPath(goldenName);
        if (!File.Exists(path))
        {
            // Allow operator to capture a fresh golden by setting an env
            // var. Otherwise hard-fail so a missing baseline is loud.
            if (Environment.GetEnvironmentVariable("B3T_CONFORMANCE_UPDATE_GOLDENS") == "true")
            {
                File.WriteAllText(path, actualJson);
                return;
            }
            throw new FileNotFoundException(
                $"Golden '{goldenName}' not found at '{path}'. Set B3T_CONFORMANCE_UPDATE_GOLDENS=true to capture.",
                path);
        }
        var expected = NormalizeWhitespace(File.ReadAllText(path));
        var actual = NormalizeWhitespace(actualJson);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            if (Environment.GetEnvironmentVariable("B3T_CONFORMANCE_UPDATE_GOLDENS") == "true")
            {
                File.WriteAllText(path, actualJson);
                return;
            }
            throw new Xunit.Sdk.XunitException(
                $"Golden mismatch for '{goldenName}'.\n--- EXPECTED ---\n{expected}\n--- ACTUAL ---\n{actual}\n");
        }
    }

    private static string NormalizeWhitespace(string s) =>
        s.Replace("\r\n", "\n").TrimEnd();

    private static string ResolveGoldenPath(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Goldens", name);
        if (File.Exists(path)) return path;

        // Walk up to the project source so an in-place `dotnet test`
        // updates the checked-in goldens (vs the bin/ copy) when the
        // operator opted into B3T_CONFORMANCE_UPDATE_GOLDENS.
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var src = Path.Combine(dir.FullName, "Goldens", name);
            if (File.Exists(src)) return src;
            var csproj = dir.GetFiles("B3.Trading.Conformance.csproj");
            if (csproj.Length > 0) return Path.Combine(dir.FullName, "Goldens", name);
            dir = dir.Parent;
        }
        return path;
    }

    public ValueTask DisposeAsync()
    {
        Http.Dispose();
        return ValueTask.CompletedTask;
    }
}
