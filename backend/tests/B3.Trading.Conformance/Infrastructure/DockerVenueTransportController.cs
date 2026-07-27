using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace B3.Trading.Conformance.Infrastructure;

internal sealed class DockerVenueTransportController
{
    private const string DefaultDockerPath = "docker";
    private const string DefaultMatchingContainer = "b3-matching-platform";
    private const string DefaultMarketDataContainer = "b3-marketdata";
    private const string DefaultTradingHostContainer = "b3-trading-host";

    private readonly string _dockerPath;
    private readonly string _matchingContainer;
    private readonly string _marketDataContainer;
    private readonly string _tradingHostContainer;

    public DockerVenueTransportController()
    {
        _dockerPath = Environment.GetEnvironmentVariable("B3T_DOCKER_PATH") ?? DefaultDockerPath;
        _matchingContainer = Environment.GetEnvironmentVariable("B3T_DOCKER_MATCHING_CONTAINER") ?? DefaultMatchingContainer;
        _marketDataContainer = Environment.GetEnvironmentVariable("B3T_DOCKER_MARKETDATA_CONTAINER") ?? DefaultMarketDataContainer;
        _tradingHostContainer = Environment.GetEnvironmentVariable("B3T_DOCKER_TRADING_HOST_CONTAINER") ?? DefaultTradingHostContainer;
    }

    public async Task<DetachedMatchingNetwork> DisconnectMatchingAsync(CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var network = await InspectNetworkAsync(_matchingContainer, ct);
        await RunDockerAsync(new[] { "network", "disconnect", "--force", network.Name, _matchingContainer }, ct);
        return new DetachedMatchingNetwork(this, network);
    }

    public async Task<PausedMatchingContainer> PauseMatchingAsync(CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);
        await RunDockerAsync(new[] { "pause", _matchingContainer }, ct);
        return new PausedMatchingContainer(this);
    }

    public async Task<DetachedMarketDataNetwork> DisconnectMarketDataAsync(CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var network = await InspectNetworkAsync(_marketDataContainer, ct);
        await RunDockerAsync(new[] { "network", "disconnect", "--force", network.Name, _marketDataContainer }, ct);
        return new DetachedMarketDataNetwork(this, network);
    }

    public async Task WaitForMarketDataTradeDrainAsync(
        DateTimeOffset sinceUtc,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await RunDockerAsync(
                new[]
                {
                    "logs", "--timestamps", "--since", sinceUtc.ToUniversalTime().AddSeconds(-10).ToString("O"),
                    _marketDataContainer,
                },
                ct);

            if (MarketDataLogShowsTradeDrain(result.StdOut, sinceUtc))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        throw new InvalidOperationException(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for marketdata trade drain in container '{_marketDataContainer}' since {sinceUtc:o}.");
    }

    public async Task WaitForMarketDataClientConnectedAsync(
        DateTimeOffset sinceUtc,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await RunDockerAsync(
                new[]
                {
                    "logs", "--timestamps", "--since", sinceUtc.ToUniversalTime().AddSeconds(-10).ToString("O"),
                    _marketDataContainer,
                },
                ct);

            if (MarketDataLogShowsClientConnected(result.StdOut, sinceUtc))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        throw new InvalidOperationException(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for marketdata client reconnect in container '{_marketDataContainer}' since {sinceUtc:o}.");
    }

    public async Task WaitForVenueOrderAbsentAsync(
        ulong? venueOrderId,
        ulong clOrdId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        string? lastSnapshot = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var priorSnapshotWriteUtc = await GetLatestMatchingSnapshotWriteUtcAsync(ct);
            var force = await RunDockerAsync(
                new[]
                {
                    "exec", _matchingContainer,
                    "wget", "-qO-", "--post-data=",
                    "http://localhost:8080/admin/channels/84/snapshot/force",
                },
                ct,
                allowNonZeroExit: true);
            if (force.ExitCode != 0)
            {
                lastError = force.StdErr;
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                continue;
            }

            DateTimeOffset? freshSnapshotWriteUtc = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                freshSnapshotWriteUtc = await GetLatestMatchingSnapshotWriteUtcAsync(ct);
                if (freshSnapshotWriteUtc is not null &&
                    (priorSnapshotWriteUtc is null ||
                     freshSnapshotWriteUtc > priorSnapshotWriteUtc))
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }

            if (freshSnapshotWriteUtc is null ||
                (priorSnapshotWriteUtc is not null &&
                 freshSnapshotWriteUtc <= priorSnapshotWriteUtc))
            {
                lastError = "forced matching snapshot did not produce a newer persisted generation";
                continue;
            }

            var snapshot = await RunDockerAsync(
                new[]
                {
                    "exec", _matchingContainer,
                    "wget", "-qO-",
                    "http://localhost:8080/admin/channels/84/snapshot",
                },
                ct,
                allowNonZeroExit: true);
            if (snapshot.ExitCode != 0)
            {
                lastError = snapshot.StdErr;
                continue;
            }

            lastSnapshot = snapshot.StdOut;
            lastError = null;
            if (!SnapshotContainsTrackedOrder(
                    snapshot.StdOut,
                    venueOrderId,
                    clOrdId))
                return;
        }

        throw new InvalidOperationException(
            $"Timed out after {timeout.TotalSeconds:F0}s proving venue order " +
            $"{venueOrderId?.ToString() ?? $"ClOrdID {clOrdId}"} absent " +
            $"from matching channel 84. lastError={lastError ?? "<none>"} " +
            $"lastSnapshot={lastSnapshot ?? "<none>"}.");
    }

    private async Task<DateTimeOffset?> GetLatestMatchingSnapshotWriteUtcAsync(
        CancellationToken ct)
    {
        var listing = await RunDockerAsync(
            new[]
            {
                "exec", _matchingContainer,
                "ls", "-1", "/var/lib/b3matching",
            },
            ct,
            allowNonZeroExit: true);
        if (listing.ExitCode != 0)
            return null;

        DateTimeOffset? latest = null;
        foreach (var fileName in listing.StdOut.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!fileName.StartsWith("channel-84.snapshot.", StringComparison.Ordinal) ||
                fileName.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            var stat = await RunDockerAsync(
                new[]
                {
                    "exec", _matchingContainer,
                    "stat", "-c", "%y",
                    $"/var/lib/b3matching/{fileName}",
                },
                ct,
                allowNonZeroExit: true);
            if (stat.ExitCode != 0 ||
                !DateTimeOffset.TryParse(
                    stat.StdOut.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var writeUtc))
            {
                continue;
            }

            if (latest is null || writeUtc > latest)
                latest = writeUtc;
        }

        return latest;
    }

    public async Task KillTradingHostAsync(CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);
        await RunDockerAsync(new[] { "kill", _tradingHostContainer }, ct);
    }

    public async Task WaitForTradingHostNotRunningAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        ContainerState? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await InspectContainerStateAsync(_tradingHostContainer, ct);
            if (!last.Running || string.Equals(last.Status, "restarting", StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        throw new InvalidOperationException(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for container '{_tradingHostContainer}' to stop running after SIGKILL. Last observed={Format(last)}.");
    }

    public async Task WaitForTradingHostRestartAsync(
        DateTimeOffset notBeforeUtc,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        ContainerState? last = null;
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await InspectContainerStateAsync(_tradingHostContainer, ct);
                lastError = null;
                if (last.Running &&
                    last.StartedAtUtc is { } startedAtUtc &&
                    startedAtUtc >= notBeforeUtc)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException)
            {
                lastError = ex.Message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        throw new InvalidOperationException(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for container '{_tradingHostContainer}' to restart after {notBeforeUtc:o}. Last observed={Format(last)} error={lastError ?? "<none>"}.");
    }

    public async Task StartTradingHostAsync(CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var result = await RunDockerAsync(new[] { "start", _tradingHostContainer }, ct, allowNonZeroExit: true);
        if (result.ExitCode != 0 &&
            !IsBenignStartConflict(result))
        {
            throw new InvalidOperationException(
                $"docker start {_tradingHostContainer} exited {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{result.StdErr}");
        }
    }

    public async Task RestartMatchingAsync(
        TimeSpan readinessTimeout,
        Func<Task>? whileRestarting = null,
        CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        using var process = StartDockerProcess(new[] { "restart", _matchingContainer });
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (whileRestarting is not null)
            await whileRestarting();

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker restart {_matchingContainer} exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }

        await WaitForContainerReadyAsync(_matchingContainer, readinessTimeout, ct);
    }

    private async Task RestartPausedMatchingAsync(
        TimeSpan readinessTimeout,
        CancellationToken ct)
    {
        await RunDockerAsync(new[] { "kill", _matchingContainer }, ct);
        var start = await RunDockerAsync(
            new[] { "start", _matchingContainer },
            ct,
            allowNonZeroExit: true);
        if (start.ExitCode != 0 && !IsBenignStartConflict(start))
        {
            throw new InvalidOperationException(
                $"docker start {_matchingContainer} exited {start.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{start.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{start.StdErr}");
        }

        await WaitForContainerReadyAsync(_matchingContainer, readinessTimeout, ct);
    }

    private async Task EnsureDockerAvailableAsync(CancellationToken ct)
    {
        await RunDockerAsync(new[] { "version", "--format", "{{.Client.Version}}" }, ct);
    }

    private static bool IsBenignStartConflict(ProcessResult result)
    {
        var combined = string.Concat(result.StdOut, "\n", result.StdErr);
        return combined.Contains("already", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("restarting", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("is starting", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<NetworkAttachment> InspectNetworkAsync(string containerName, CancellationToken ct)
    {
        var result = await RunDockerAsync(new[] { "inspect", containerName }, ct);
        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement[0];
        var networks = root.GetProperty("NetworkSettings").GetProperty("Networks");
        foreach (var network in networks.EnumerateObject())
        {
            var aliases = new List<string>();
            if (network.Value.TryGetProperty("Aliases", out var aliasesProp) &&
                aliasesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var alias in aliasesProp.EnumerateArray())
                {
                    var value = alias.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        aliases.Add(value);
                }
            }

            var ipv4Address = network.Value.TryGetProperty("IPAddress", out var ipProp)
                              && ipProp.ValueKind == JsonValueKind.String
                ? string.IsNullOrWhiteSpace(ipProp.GetString())
                    ? null
                    : ipProp.GetString()!.Trim()
                : null;

            if (aliases.Count == 0)
                aliases.Add(containerName);

            return new NetworkAttachment(
                network.Name,
                aliases.Distinct(StringComparer.Ordinal).ToArray(),
                ipv4Address);
        }

        throw new InvalidOperationException($"Container '{containerName}' is not attached to any Docker network.");
    }

    private async Task WaitForContainerReadyAsync(
        string containerName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await InspectContainerStateAsync(containerName, ct);
            if (state.Running && (state.HealthStatus is null || string.Equals(state.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        var last = await InspectContainerStateAsync(containerName, ct);
        throw new InvalidOperationException(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for container '{containerName}' to become ready. Last observed={Format(last)}.");
    }

    private async Task<ContainerState> InspectContainerStateAsync(string containerName, CancellationToken ct)
    {
        var result = await RunDockerAsync(new[] { "inspect", containerName }, ct);
        using var doc = JsonDocument.Parse(result.StdOut);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return new ContainerState(false, null, null, null);

        var root = doc.RootElement[0];
        if (!root.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.Object)
            return new ContainerState(false, null, null, null);

        var running = state.TryGetProperty("Running", out var runningProp) && runningProp.ValueKind == JsonValueKind.True;
        var status = state.TryGetProperty("Status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
            ? statusProp.GetString()
            : null;
        string? healthStatus = null;
        if (state.TryGetProperty("Health", out var health) &&
            health.ValueKind == JsonValueKind.Object &&
            health.TryGetProperty("Status", out var healthStatusProp) &&
            healthStatusProp.ValueKind == JsonValueKind.String)
        {
            healthStatus = healthStatusProp.GetString();
        }

        DateTimeOffset? startedAtUtc = null;
        if (state.TryGetProperty("StartedAt", out var startedAtProp) &&
            startedAtProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                startedAtProp.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var startedAt))
        {
            startedAtUtc = startedAt;
        }

        return new ContainerState(running, healthStatus, status, startedAtUtc);
    }

    private async Task<ProcessResult> RunDockerAsync(
        IEnumerable<string> args,
        CancellationToken ct,
        bool allowNonZeroExit = false)
    {
        using var process = StartDockerProcess(args);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!allowNonZeroExit && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', args)} exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private Process StartDockerProcess(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _dockerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException($"Failed to start '{_dockerPath}'.");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Failed to start docker CLI '{_dockerPath}'. This spec requires docker CLI/socket access in the test process.",
                ex);
        }

        return process;
    }

    private async Task ConnectContainerAsync(
        string containerName,
        NetworkAttachment network,
        CancellationToken ct)
    {
        try
        {
            await RunDockerAsync(BuildConnectArgs(containerName, network, includeIp: true), ct);
        }
        catch (InvalidOperationException ex) when (!string.IsNullOrWhiteSpace(network.Ipv4Address)
                                                  && ShouldRetryWithoutStaticIp(ex.Message))
        {
            await RunDockerAsync(BuildConnectArgs(containerName, network, includeIp: false), ct);
        }
    }

    private static bool ShouldRetryWithoutStaticIp(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Address already in use", StringComparison.Ordinal)
            || message.Contains("user specified IP address is supported only when connecting to networks with user configured subnets", StringComparison.Ordinal);
    }

    private static bool MarketDataLogShowsTradeDrain(string logs, DateTimeOffset sinceUtc)
    {
        var baseline = new TradeCounters(0, 0, 0);
        var sawBaseline = false;
        var sawTradeWindow = false;
        using var reader = new StringReader(logs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseTradeCounters(line, out var timestampUtc, out var counters))
            {
                if (timestampUtc < sinceUtc)
                {
                    baseline = counters;
                    sawBaseline = true;
                    continue;
                }

                if (!sawBaseline || counters.Sbe > baseline.Sbe || counters.Recv > baseline.Recv || counters.Emit > baseline.Emit)
                    sawTradeWindow = true;
                continue;
            }

            if (sawTradeWindow &&
                TryParseTimestamp(line, out timestampUtc) &&
                timestampUtc >= sinceUtc &&
                line.Contains("per-symbol:", StringComparison.Ordinal) &&
                !line.Contains("gate:on", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseTradeCounters(string line, out DateTimeOffset timestampUtc, out TradeCounters counters)
    {
        timestampUtc = default;
        counters = default;
        if (!TryParseTimestamp(line, out timestampUtc) || !line.Contains("trades:", StringComparison.Ordinal))
            return false;

        if (!TryParseCounter(line, "sbe=", out var sbe) ||
            !TryParseCounter(line, "recv=", out var recv) ||
            !TryParseCounter(line, "emit=", out var emit))
        {
            return false;
        }

        counters = new TradeCounters(sbe, recv, emit);
        return true;
    }

    private static bool MarketDataLogShowsClientConnected(string logs, DateTimeOffset sinceUtc)
    {
        using var reader = new StringReader(logs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseTimestamp(line, out var timestampUtc) &&
                timestampUtc >= sinceUtc &&
                line.Contains("Client ", StringComparison.Ordinal) &&
                line.Contains(" connected", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool SnapshotContainsTrackedOrder(
        string snapshotJson,
        ulong? venueOrderId,
        ulong clOrdId)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        if (!TryGetPropertyIgnoreCase(document.RootElement, "Engine", out var engine) ||
            !TryGetPropertyIgnoreCase(engine, "Books", out var books) ||
            books.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Matching snapshot did not contain engine.books.");
        }

        foreach (var book in books.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(book, "Orders", out var orders) ||
                orders.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var order in orders.EnumerateArray())
            {
                if (TryGetPropertyIgnoreCase(order, "OrderId", out var orderId) &&
                    orderId.TryGetUInt64(out var parsed) &&
                    venueOrderId is { } expectedVenueOrderId &&
                    parsed == expectedVenueOrderId)
                {
                    return true;
                }
                if (venueOrderId is null &&
                    TryGetPropertyIgnoreCase(order, "ClOrdId", out var clOrdIdProperty) &&
                    clOrdIdProperty.ValueKind == JsonValueKind.String &&
                    ulong.TryParse(clOrdIdProperty.GetString(), out var parsedClOrdId) &&
                    parsedClOrdId == clOrdId &&
                    TryGetPropertyIgnoreCase(order, "EnteringFirm", out var enteringFirm) &&
                    enteringFirm.TryGetUInt32(out var parsedFirm) &&
                    parsedFirm == 100)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseTimestamp(string line, out DateTimeOffset timestampUtc)
    {
        timestampUtc = default;
        var space = line.IndexOf(' ');
        if (space <= 0)
            return false;

        return DateTimeOffset.TryParse(
            line[..space],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestampUtc);
    }

    private static bool TryParseCounter(string line, string marker, out int value)
    {
        value = 0;
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return false;

        start += marker.Length;
        var end = start;
        while (end < line.Length && char.IsDigit(line[end]))
            end++;

        return end > start && int.TryParse(line[start..end], out value);
    }

    internal sealed record NetworkAttachment(string Name, IReadOnlyList<string> Aliases, string? Ipv4Address);
    private sealed record ContainerState(bool Running, string? HealthStatus, string? Status, DateTimeOffset? StartedAtUtc);
    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
    private readonly record struct TradeCounters(int Sbe, int Recv, int Emit);

    private static string Format(ContainerState? state) => state is null
        ? "<missing>"
        : $"{{ running={state.Running}, health={state.HealthStatus ?? "null"}, status={state.Status ?? "null"}, startedAtUtc={state.StartedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "null"} }}";

    private static List<string> BuildConnectArgs(
        string containerName,
        NetworkAttachment network,
        bool includeIp)
    {
        var connectArgs = new List<string> { "network", "connect" };
        foreach (var alias in network.Aliases)
        {
            connectArgs.Add("--alias");
            connectArgs.Add(alias);
        }

        if (includeIp && !string.IsNullOrWhiteSpace(network.Ipv4Address))
        {
            connectArgs.Add("--ip");
            connectArgs.Add(network.Ipv4Address);
        }

        connectArgs.Add(network.Name);
        connectArgs.Add(containerName);
        return connectArgs;
    }

    internal sealed class DetachedMatchingNetwork : IAsyncDisposable
    {
        private readonly DockerVenueTransportController _owner;
        private readonly NetworkAttachment _network;
        private bool _reconnected;

        internal DetachedMatchingNetwork(DockerVenueTransportController owner, NetworkAttachment network)
        {
            _owner = owner;
            _network = network;
        }

        public async Task ReconnectAsync(CancellationToken ct = default)
        {
            if (_reconnected)
                return;

            await _owner.ConnectContainerAsync(_owner._matchingContainer, _network, ct);
            _reconnected = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_reconnected)
                await ReconnectAsync();
        }
    }

    internal sealed class PausedMatchingContainer : IAsyncDisposable
    {
        private readonly DockerVenueTransportController _owner;
        private bool _resumed;

        internal PausedMatchingContainer(DockerVenueTransportController owner)
        {
            _owner = owner;
        }

        public async Task RestartAsync(
            TimeSpan readinessTimeout,
            CancellationToken ct = default)
        {
            if (_resumed)
                return;

            await _owner.RestartPausedMatchingAsync(readinessTimeout, ct);
            _resumed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_resumed)
                return;

            await _owner.RunDockerAsync(
                new[] { "unpause", _owner._matchingContainer },
                CancellationToken.None,
                allowNonZeroExit: true);
        }
    }

    internal sealed class DetachedMarketDataNetwork : IAsyncDisposable
    {
        private readonly DockerVenueTransportController _owner;
        private readonly NetworkAttachment _network;
        private bool _reconnected;

        internal DetachedMarketDataNetwork(DockerVenueTransportController owner, NetworkAttachment network)
        {
            _owner = owner;
            _network = network;
        }

        public async Task ReconnectAsync(CancellationToken ct = default)
        {
            if (_reconnected)
                return;

            await _owner.ConnectContainerAsync(_owner._marketDataContainer, _network, ct);
            _reconnected = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_reconnected)
                await ReconnectAsync();
        }
    }
}
