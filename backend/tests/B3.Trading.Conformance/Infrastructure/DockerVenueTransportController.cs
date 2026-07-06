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

    private readonly string _dockerPath;
    private readonly string _matchingContainer;
    private readonly string _marketDataContainer;

    public DockerVenueTransportController()
    {
        _dockerPath = Environment.GetEnvironmentVariable("B3T_DOCKER_PATH") ?? DefaultDockerPath;
        _matchingContainer = Environment.GetEnvironmentVariable("B3T_DOCKER_MATCHING_CONTAINER") ?? DefaultMatchingContainer;
        _marketDataContainer = Environment.GetEnvironmentVariable("B3T_DOCKER_MARKETDATA_CONTAINER") ?? DefaultMarketDataContainer;
    }

    public async Task<DetachedMatchingNetwork> DisconnectMatchingAsync(CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var network = await InspectNetworkAsync(_matchingContainer, ct);
        await RunDockerAsync(new[] { "network", "disconnect", "--force", network.Name, _matchingContainer }, ct);
        return new DetachedMatchingNetwork(this, network);
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

    private async Task EnsureDockerAvailableAsync(CancellationToken ct)
    {
        await RunDockerAsync(new[] { "version", "--format", "{{.Client.Version}}" }, ct);
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

    private async Task<ProcessResult> RunDockerAsync(
        IEnumerable<string> args,
        CancellationToken ct,
        bool allowNonZeroExit = false)
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

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start '{_dockerPath}'.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start docker CLI '{_dockerPath}'. This spec requires docker CLI/socket access in the test process.",
                ex);
        }

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
                                                  && ex.Message.Contains("Address already in use", StringComparison.Ordinal))
        {
            await RunDockerAsync(BuildConnectArgs(containerName, network, includeIp: false), ct);
        }
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
    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
    private readonly record struct TradeCounters(int Sbe, int Recv, int Emit);

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
