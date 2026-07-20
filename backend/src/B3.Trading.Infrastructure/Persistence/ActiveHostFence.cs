using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using B3.Trading.Application.Outbound;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure.Persistence;

public sealed class ActiveHostFence : IDisposable
{
    private const int EpochFileVersion = 1;

    private readonly object _gate = new();
    private readonly PersistenceOptions _persistence;
    private readonly ExchangeOptions _exchange;
    private readonly OutboundProcessEpoch _epoch;
    private readonly OutboundRecoveryState _recovery;
    private readonly ILogger<ActiveHostFence> _logger;
    private readonly List<FileStream> _leases = new();
    private bool _attempted;
    private bool _disposed;
    private Exception? _failure;

    public ActiveHostFence(
        IOptions<PersistenceOptions> persistence,
        IOptions<ExchangeOptions> exchange,
        OutboundProcessEpoch epoch,
        OutboundRecoveryState recovery,
        ILogger<ActiveHostFence> logger)
    {
        _persistence = persistence.Value;
        _exchange = exchange.Value;
        _epoch = epoch;
        _recovery = recovery;
        _logger = logger;
        RequiredFirmIds = ResolveRequiredFirms(_persistence, _exchange);
        _recovery.ConfigureRequiredFirms(RequiredFirmIds);
    }

    public IReadOnlyList<string> RequiredFirmIds { get; }

    public bool IsHeld
    {
        get
        {
            lock (_gate)
                return _attempted && _failure is null && !_disposed;
        }
    }

    public Exception? Failure
    {
        get
        {
            lock (_gate)
                return _failure;
        }
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attempted)
                return _failure is null;
            _attempted = true;

            try
            {
                if (!_persistence.Enabled)
                {
                    _epoch.Initialize(ProcessEpochId.New(), 1);
                    return true;
                }

                var deploymentRoot = ResolveDeploymentRoot(_persistence);
                var fenceRoot = Path.Combine(deploymentRoot, "active-host");
                Directory.CreateDirectory(fenceRoot);
                foreach (var firmId in RequiredFirmIds.OrderBy(static firm => firm, StringComparer.Ordinal))
                {
                    var path = Path.Combine(fenceRoot, $"{HashFirmId(firmId)}.lock");
                    var lease = new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.WriteThrough);
                    _leases.Add(lease);
                }

                var epoch = AdvanceEpoch(deploymentRoot);
                _epoch.Initialize(new ProcessEpochId(epoch.EpochId), epoch.Sequence);
                _logger.LogInformation(
                    "Acquired exclusive active-host fence for {FirmCount} firm(s) at durable process epoch {EpochSequence}.",
                    RequiredFirmIds.Count,
                    epoch.Sequence);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or InvalidDataException
                    or OverflowException
                    or ArgumentException
                    or NotSupportedException
                    or System.Security.SecurityException)
            {
                ReleaseLeasesUnsafe();
                _failure = ex;
                _recovery.FailFence(ex.GetType().Name);
                _logger.LogCritical(
                    ex,
                    "Exclusive active-host fence or durable process epoch acquisition failed; venue connection and readiness remain closed.");
                return false;
            }
        }
    }

    public void RecordStorageFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _failure ??= exception;
            _recovery.Fail(exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseLeasesUnsafe();
        }
    }

    private EpochDocument AdvanceEpoch(string deploymentRoot)
    {
        var epochPath = Path.Combine(deploymentRoot, "process-epoch.json");
        var stagingPath = Path.Combine(deploymentRoot, "process-epoch.json.writing");
        long previous = 0;
        if (File.Exists(epochPath))
        {
            EpochDocument? stored;
            try
            {
                stored = JsonSerializer.Deserialize<EpochDocument>(File.ReadAllBytes(epochPath));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The durable process epoch file is corrupt.", ex);
            }
            if (stored is null
                || stored.Version != EpochFileVersion
                || stored.Sequence <= 0
                || stored.EpochId == Guid.Empty)
            {
                throw new InvalidDataException("The durable process epoch file is invalid.");
            }
            previous = stored.Sequence;
        }

        var next = new EpochDocument
        {
            Version = EpochFileVersion,
            Sequence = checked(previous + 1),
            EpochId = Guid.NewGuid(),
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(next);
        using (var stream = new FileStream(
                   stagingPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        File.Move(stagingPath, epochPath, overwrite: true);
        return next;
    }

    private void ReleaseLeasesUnsafe()
    {
        foreach (var lease in _leases)
            lease.Dispose();
        _leases.Clear();
    }

    private static IReadOnlyList<string> ResolveRequiredFirms(
        PersistenceOptions persistence,
        ExchangeOptions exchange)
    {
        var firms = exchange.Firms
            .Select(static firm => firm.FirmId)
            .Where(static firm => !string.IsNullOrWhiteSpace(firm))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return firms.Length > 0 ? firms : [persistence.FirmId];
    }

    private static string ResolveDeploymentRoot(PersistenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FirmId)
            || Path.IsPathRooted(options.FirmId)
            || options.FirmId is "." or ".."
            || options.FirmId.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException("Persistence FirmId must be a relative non-empty path segment.");
        }

        var dataRoot = Path.GetFullPath(options.DataDirectory);
        var deploymentRoot = Path.GetFullPath(Path.Combine(dataRoot, options.FirmId));
        if (!deploymentRoot.StartsWith(
                dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persistence FirmId escapes DataDirectory.");
        }
        Directory.CreateDirectory(deploymentRoot);
        return deploymentRoot;
    }

    private static string HashFirmId(string firmId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(firmId)))
            .ToLowerInvariant();

    private sealed class EpochDocument
    {
        public int Version { get; init; }
        public long Sequence { get; init; }
        public Guid EpochId { get; init; }
    }
}
