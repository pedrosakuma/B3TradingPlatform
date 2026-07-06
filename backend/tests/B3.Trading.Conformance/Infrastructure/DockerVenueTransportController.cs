using System.Diagnostics;
using System.Text.Json;

namespace B3.Trading.Conformance.Infrastructure;

internal sealed class DockerVenueTransportController
{
    private const string DefaultDockerPath = "docker";
    private const string DefaultMatchingContainer = "b3-matching-platform";

    private readonly string _dockerPath;
    private readonly string _matchingContainer;

    public DockerVenueTransportController()
    {
        _dockerPath = Environment.GetEnvironmentVariable("B3T_DOCKER_PATH") ?? DefaultDockerPath;
        _matchingContainer = Environment.GetEnvironmentVariable("B3T_DOCKER_MATCHING_CONTAINER") ?? DefaultMatchingContainer;
    }

    public async Task<DetachedMatchingNetwork> DisconnectMatchingAsync(CancellationToken ct = default)
    {
        await EnsureDockerAvailableAsync(ct);

        var network = await InspectMatchingNetworkAsync(ct);
        await RunDockerAsync(new[] { "network", "disconnect", "--force", network.Name, _matchingContainer }, ct);
        return new DetachedMatchingNetwork(this, network);
    }

    private async Task EnsureDockerAvailableAsync(CancellationToken ct)
    {
        await RunDockerAsync(new[] { "version", "--format", "{{.Client.Version}}" }, ct);
    }

    private async Task<NetworkAttachment> InspectMatchingNetworkAsync(CancellationToken ct)
    {
        var result = await RunDockerAsync(new[] { "inspect", _matchingContainer }, ct);
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

            if (aliases.Count == 0)
                aliases.Add(_matchingContainer);

            return new NetworkAttachment(network.Name, aliases.Distinct(StringComparer.Ordinal).ToArray());
        }

        throw new InvalidOperationException($"Container '{_matchingContainer}' is not attached to any Docker network.");
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

    internal sealed record NetworkAttachment(string Name, IReadOnlyList<string> Aliases);
    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

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

            var connectArgs = new List<string> { "network", "connect" };
            foreach (var alias in _network.Aliases)
            {
                connectArgs.Add("--alias");
                connectArgs.Add(alias);
            }

            connectArgs.Add(_network.Name);
            connectArgs.Add(_owner._matchingContainer);
            await _owner.RunDockerAsync(connectArgs, ct);
            _reconnected = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_reconnected)
                await ReconnectAsync();
        }
    }
}
