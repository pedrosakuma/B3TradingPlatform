using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

/// <summary>
/// Slice 3 of #97 hardening: file-backed <see cref="IUserStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Env-seeded users (from <see cref="AuthOptions.Users"/>) live in an
/// immutable in-memory dictionary and are NEVER written to disk —
/// configuration is authoritative and the file is only the runtime
/// signup tail. Runtime users are loaded on construction (boot) and
/// written through on every successful <see cref="TryAdd"/>.
/// </para>
/// <para>
/// On-disk format is JSON with a top-level envelope so future schema
/// evolutions can migrate without breaking startup:
/// <code>
/// { "version": 1, "users": [ { "Username": "...", "PasswordHash": "...",
///                              "Salt": "...", "Iterations": 600000,
///                              "Role": "user", "Firm": "FIRM01" }, ... ] }
/// </code>
/// </para>
/// <para>
/// Writes are atomic (write to <c>users.json.tmp</c> + fsync + rename)
/// and serialized via a single <c>lock</c> so concurrent signups can't
/// corrupt the file. A corrupt file is logged at WARN level and treated
/// as empty so a poisoned file does not brick boot — operators inspect
/// the warning and either restore from backup or delete the file.
/// </para>
/// </remarks>
public sealed class FileBackedUserStore : IUserStore
{
    private readonly Dictionary<string, UserConfig> _seeded;
    private readonly ConcurrentDictionary<string, UserConfig> _runtime =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    private readonly object _writeGate = new();
    private readonly ILogger<FileBackedUserStore> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public FileBackedUserStore(
        IOptions<AuthOptions> authOptions,
        IOptions<UserStoreOptions> storeOptions,
        ILogger<FileBackedUserStore> logger)
    {
        ArgumentNullException.ThrowIfNull(authOptions);
        ArgumentNullException.ThrowIfNull(storeOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var path = storeOptions.Value.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "FileBackedUserStore requires Trading:Auth:UserStore:FilePath. " +
                "Either set it explicitly or rely on the host's default derivation " +
                "from Trading:Persistence:DataDirectory.");
        _filePath = path;

        _seeded = new Dictionary<string, UserConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in authOptions.Value.Users)
        {
            if (string.IsNullOrWhiteSpace(u.Username)) continue;
            _seeded[u.Username] = u;
        }

        LoadRuntimeUsers();
    }

    public bool TryGet(string username, out UserConfig? user)
    {
        if (string.IsNullOrWhiteSpace(username)) { user = null; return false; }
        if (_seeded.TryGetValue(username, out var s)) { user = s; return true; }
        if (_runtime.TryGetValue(username, out var r)) { user = r; return true; }
        user = null;
        return false;
    }

    public bool TryAdd(UserConfig user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.Username)) return false;

        // Block runtime collisions with env-seeded users — same invariant
        // as InMemoryUserStore.
        if (_seeded.ContainsKey(user.Username)) return false;

        // Serialize TryAdd through the write gate so the in-memory state
        // and the on-disk snapshot can never disagree under concurrent
        // signups. The signup endpoint is rate-limited and rare; this is
        // not a hot path.
        lock (_writeGate)
        {
            if (!_runtime.TryAdd(user.Username, user))
                return false;

            try
            {
                PersistRuntimeUsersLocked();
            }
            catch (Exception ex)
            {
                // Roll back the in-memory insert so the next signup with
                // the same username sees a 409 (or succeeds and retries
                // the write) rather than a silent disk-vs-memory drift.
                _runtime.TryRemove(user.Username, out _);
                _logger.LogError(ex,
                    "FileBackedUserStore: failed to persist runtime user {Username} to {Path}; " +
                    "in-memory insert rolled back.",
                    user.Username, _filePath);
                throw;
            }

            return true;
        }
    }

    public bool TryUpdate(UserConfig user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.Username)) return false;

        lock (_writeGate)
        {
            // Env-seeded users: mutate in place (config remains
            // authoritative for credentials; TOTP overlay survives only
            // process lifetime — matches InMemoryUserStore).
            if (_seeded.TryGetValue(user.Username, out var seeded))
            {
                seeded.Totp = user.Totp;
                seeded.Require2FA = user.Require2FA;
                return true;
            }

            if (!_runtime.ContainsKey(user.Username)) return false;
            var previous = _runtime[user.Username];
            _runtime[user.Username] = user;

            try
            {
                PersistRuntimeUsersLocked();
            }
            catch (Exception ex)
            {
                // Roll the in-memory state back to the prior snapshot
                // so we don't expose an unpersisted TOTP secret.
                _runtime[user.Username] = previous;
                _logger.LogError(ex,
                    "FileBackedUserStore: failed to persist updated user {Username} to {Path}; " +
                    "in-memory update rolled back.",
                    user.Username, _filePath);
                throw;
            }

            return true;
        }
    }

    public bool TryRecordTotpUse(string username, long matchedStep, out UserConfig? updatedUser)
    {
        updatedUser = null;
        if (string.IsNullOrWhiteSpace(username)) return false;

        lock (_writeGate)
        {
            if (!TryGet(username, out var user) || user is null || user.Totp is null
                || user.Totp.EnrolledAt is null)
                return false;

            if (user.Totp.LastUsedTimeStep is { } prev && matchedStep <= prev)
                return false;

            var previous = user.Totp.LastUsedTimeStep;
            user.Totp.LastUsedTimeStep = matchedStep;

            // Env-seeded users are mutated in place only — no disk
            // write (config is authoritative on restart).
            if (_seeded.ContainsKey(username))
            {
                updatedUser = user;
                return true;
            }

            try
            {
                PersistRuntimeUsersLocked();
            }
            catch (Exception ex)
            {
                user.Totp.LastUsedTimeStep = previous;
                _logger.LogError(ex,
                    "FileBackedUserStore: failed to persist TOTP time step for {Username} to {Path}; " +
                    "in-memory update rolled back.",
                    username, _filePath);
                throw;
            }

            updatedUser = user;
            return true;
        }
    }

    public RecoveryCodeConsumeResult TryConsumeRecoveryCode(string username, string codeHash, out UserConfig? updatedUser)
    {
        updatedUser = null;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(codeHash))
            return RecoveryCodeConsumeResult.NotFound;

        lock (_writeGate)
        {
            if (!TryGet(username, out var user) || user is null || user.Totp is null)
                return RecoveryCodeConsumeResult.NotFound;

            // Constant-time-ish scan; full pass so timing reveals
            // nothing about which slot matched.
            var idx = -1;
            for (var i = 0; i < user.Totp.RecoveryCodes.Count; i++)
            {
                if (string.Equals(user.Totp.RecoveryCodes[i], codeHash, StringComparison.Ordinal))
                    idx = i;
            }
            if (idx < 0)
            {
                for (var i = 0; i < user.Totp.ConsumedRecoveryCodes.Count; i++)
                {
                    if (string.Equals(user.Totp.ConsumedRecoveryCodes[i], codeHash, StringComparison.Ordinal))
                        return RecoveryCodeConsumeResult.AlreadyConsumed;
                }
                return RecoveryCodeConsumeResult.NotFound;
            }

            var removed = user.Totp.RecoveryCodes[idx];
            user.Totp.RecoveryCodes.RemoveAt(idx);

            // Snapshot the consumed list so we can roll back atomically
            // alongside the removal on a failed disk write.
            var consumedBefore = new List<string>(user.Totp.ConsumedRecoveryCodes);
            AppendConsumed(user.Totp, codeHash);

            if (_seeded.ContainsKey(username))
            {
                updatedUser = user;
                return RecoveryCodeConsumeResult.Consumed;
            }

            try
            {
                PersistRuntimeUsersLocked();
            }
            catch (Exception ex)
            {
                user.Totp.RecoveryCodes.Insert(idx, removed);
                user.Totp.ConsumedRecoveryCodes.Clear();
                user.Totp.ConsumedRecoveryCodes.AddRange(consumedBefore);
                _logger.LogError(ex,
                    "FileBackedUserStore: failed to persist recovery-code consumption for {Username} to {Path}; " +
                    "in-memory update rolled back.",
                    username, _filePath);
                throw;
            }

            updatedUser = user;
            return RecoveryCodeConsumeResult.Consumed;
        }
    }

    private static void AppendConsumed(UserTotpConfig totp, string codeHash)
    {
        while (totp.ConsumedRecoveryCodes.Count >= UserTotpConfig.ConsumedRecoveryCodesCap)
            totp.ConsumedRecoveryCodes.RemoveAt(0);
        totp.ConsumedRecoveryCodes.Add(codeHash);
    }

    private void LoadRuntimeUsers()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation(
                "FileBackedUserStore: no runtime user file at {Path}; starting with empty runtime set.",
                _filePath);
            return;
        }

        try
        {
            using var stream = File.OpenRead(_filePath);
            var envelope = JsonSerializer.Deserialize<UserStoreFileEnvelope>(stream, JsonOpts);
            if (envelope?.Users is null)
            {
                _logger.LogWarning(
                    "FileBackedUserStore: {Path} parsed but contained no users; starting empty.",
                    _filePath);
                return;
            }

            foreach (var u in envelope.Users)
            {
                if (string.IsNullOrWhiteSpace(u.Username)) continue;
                if (_seeded.ContainsKey(u.Username)) continue; // env wins
                _runtime[u.Username] = u;
            }
            _logger.LogInformation(
                "FileBackedUserStore: loaded {Count} runtime users from {Path}.",
                _runtime.Count, _filePath);
        }
        catch (Exception ex)
        {
            // Don't brick boot — log loud and start empty. Operators
            // inspect the warning to decide between restore from backup
            // or accepting that runtime users are gone.
            _logger.LogWarning(ex,
                "FileBackedUserStore: failed to read {Path}; starting with empty runtime set. " +
                "Existing runtime signups (if any) are NOT loaded — operator action required.",
                _filePath);
        }
    }

    private void PersistRuntimeUsersLocked()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var envelope = new UserStoreFileEnvelope
        {
            Version = 1,
            Users = _runtime.Values
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        // Atomic write: tmp + fsync + rename. Crash between fsync and
        // rename leaves the previous good file intact; crash after
        // rename is fine because the new file is on disk.
        var tmp = _filePath + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, envelope, JsonOpts);
            stream.Flush(true);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    private sealed class UserStoreFileEnvelope
    {
        public int Version { get; set; }
        public List<UserConfig> Users { get; set; } = new();
    }
}
