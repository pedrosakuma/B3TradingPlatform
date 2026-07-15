namespace B3.Trading.Application.Identity;

public sealed class InMemoryTradingUserDirectory : ITradingUserDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MutableUser> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Issuer, string Subject), long> _bindingIds = new();
    private long _nextBindingId = 1;
    private bool _initialized;

    public string ProviderName => "InMemory";
    public string? StorePath => null;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _initialized = true;
        return Task.CompletedTask;
    }

    public Task<TradingUserDirectoryHealth> CheckHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(new TradingUserDirectoryHealth(_initialized, ProviderName, null, 1, _initialized ? null : "not_initialized"));

    public Task<TradingUser?> GetUserAsync(string tradingUserId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.TryGetValue(tradingUserId, out var user) ? Snapshot(user) : null);
        }
    }

    public Task<TradingUser?> ResolveExternalIdentityAsync(string issuer, string subject, CancellationToken ct = default)
    {
        ValidateOpaque("issuer", issuer);
        ValidateOpaque("subject", subject);
        lock (_gate)
        {
            if (!_bindingIds.TryGetValue((issuer, subject), out var id))
                return Task.FromResult<TradingUser?>(null);
            var user = _users.Values.FirstOrDefault(u => u.Bindings.Any(b => b.Id == id));
            return Task.FromResult(user is null ? null : Snapshot(user));
        }
    }

    public Task<IReadOnlyList<TradingUser>> ListUsersAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TradingUser>>(
                _users.Values.OrderBy(u => u.TradingUserId, StringComparer.Ordinal).Select(Snapshot).ToArray());
        }
    }

    public Task<int> ImportLegacyUsersAsync(IReadOnlyCollection<LegacyTradingUserImport> users, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        ValidateLegacyBatch(users);

        var now = DateTimeOffset.UtcNow;
        var inserted = 0;
        lock (_gate)
        {
            foreach (var import in users)
            {
                var existingCollision = _users.Keys.FirstOrDefault(id =>
                    string.Equals(id, import.TradingUserId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(id, import.TradingUserId, StringComparison.Ordinal));
                if (existingCollision is not null)
                    throw new TradingUserDirectoryValidationException("Legacy import would create a case-insensitive trading user ID collision.");
            }

            foreach (var import in users)
            {
                if (_users.ContainsKey(import.TradingUserId))
                    continue;

                _users.Add(import.TradingUserId, new MutableUser
                {
                    TradingUserId = import.TradingUserId,
                    DisplayName = import.DisplayName,
                    FirmId = import.FirmId,
                    Status = TradingUserDirectoryConstants.StatusActive,
                    Role = import.Role,
                    CreatedAt = now,
                    UpdatedAt = now,
                    RowVersion = 1,
                });
                inserted++;
            }
        }

        return Task.FromResult(inserted);
    }

    public Task<ExternalIdentityBinding> BindExternalIdentityAsync(
        string tradingUserId,
        ExternalIdentityBindingRequest binding,
        long expectedRowVersion,
        CancellationToken ct = default)
    {
        ValidateTradingUserId(tradingUserId);
        ValidateBinding(binding);
        lock (_gate)
        {
            var user = RequireUserAndVersion(tradingUserId, expectedRowVersion);
            if (_bindingIds.ContainsKey((binding.Issuer, binding.Subject)))
                throw new TradingUserDirectoryConflictException("External identity is already bound.");

            var createdAt = DateTimeOffset.UtcNow;
            var row = new ExternalIdentityBinding(
                _nextBindingId++,
                binding.Issuer,
                binding.Subject,
                tradingUserId,
                BlankToNull(binding.TenantId),
                BlankToNull(binding.ObjectId),
                createdAt);
            user.Bindings.Add(row);
            user.RowVersion++;
            user.UpdatedAt = createdAt;
            _bindingIds.Add((binding.Issuer, binding.Subject), row.Id);
            return Task.FromResult(row);
        }
    }

    public Task UnbindExternalIdentityAsync(string tradingUserId, long bindingId, long expectedRowVersion, CancellationToken ct = default)
    {
        ValidateTradingUserId(tradingUserId);
        lock (_gate)
        {
            var user = RequireUserAndVersion(tradingUserId, expectedRowVersion);
            var idx = user.Bindings.FindIndex(b => b.Id == bindingId);
            if (idx < 0)
                throw new TradingUserDirectoryConflictException("External identity binding does not exist for the user.");
            var binding = user.Bindings[idx];
            user.Bindings.RemoveAt(idx);
            _bindingIds.Remove((binding.Issuer, binding.Subject));
            user.RowVersion++;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task SetStatusAsync(string tradingUserId, string status, long expectedRowVersion, CancellationToken ct = default)
    {
        ValidateTradingUserId(tradingUserId);
        if (!TradingUserDirectoryConstants.IsValidStatus(status))
            throw new TradingUserDirectoryValidationException("Invalid trading user status.");

        lock (_gate)
        {
            var user = RequireUserAndVersion(tradingUserId, expectedRowVersion);
            user.Status = status;
            user.RowVersion++;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task SetFirmAndRoleAsync(
        string tradingUserId,
        string firmId,
        string role,
        long expectedRowVersion,
        CancellationToken ct = default)
    {
        ValidateTradingUserId(tradingUserId);
        ValidateFirmAndRole(firmId, role);

        lock (_gate)
        {
            var user = RequireUserAndVersion(tradingUserId, expectedRowVersion);
            user.FirmId = firmId;
            user.Role = role;
            user.RowVersion++;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task<TradingUserDirectoryBackup> CreateBackupAsync(string destinationPath, CancellationToken ct = default)
    {
        throw new NotSupportedException("The in-memory identity directory has no durable backup representation.");
    }

    internal static void ValidateLegacyBatch(IEnumerable<LegacyTradingUserImport> imports)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in imports)
        {
            ValidateTradingUserId(import.TradingUserId);
            if (!seen.Add(import.TradingUserId))
                throw new TradingUserDirectoryValidationException("Legacy import contains case-insensitive trading user ID collisions.");
            if (string.IsNullOrWhiteSpace(import.DisplayName))
                throw new TradingUserDirectoryValidationException("Legacy import display name is required.");
            ValidateFirmAndRole(import.FirmId, import.Role);
        }
    }

    internal static void ValidateTradingUserId(string tradingUserId)
    {
        if (string.IsNullOrWhiteSpace(tradingUserId)
            || tradingUserId.Length > TradingUserDirectoryConstants.MaxTradingUserIdLength)
            throw new TradingUserDirectoryValidationException("Trading user ID must be non-empty and at most 64 characters.");
    }

    internal static void ValidateFirmAndRole(string firmId, string role)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new TradingUserDirectoryValidationException("Firm is required.");
        if (!TradingUserDirectoryConstants.IsValidRole(role))
            throw new TradingUserDirectoryValidationException("Invalid trading user role.");
    }

    internal static void ValidateBinding(ExternalIdentityBindingRequest binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateOpaque("issuer", binding.Issuer);
        ValidateOpaque("subject", binding.Subject);
    }

    private static void ValidateOpaque(string name, string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new TradingUserDirectoryValidationException($"{name} is required.");
    }

    private MutableUser RequireUserAndVersion(string tradingUserId, long expectedRowVersion)
    {
        if (!_users.TryGetValue(tradingUserId, out var user))
            throw new TradingUserDirectoryConflictException("Trading user does not exist.");
        if (user.RowVersion != expectedRowVersion)
            throw new TradingUserDirectoryConcurrencyException("Trading user row version is stale.");
        return user;
    }

    private static TradingUser Snapshot(MutableUser user) =>
        new(
            user.TradingUserId,
            user.DisplayName,
            user.FirmId,
            user.Status,
            user.Role,
            user.RowVersion,
            user.CreatedAt,
            user.UpdatedAt,
            user.Bindings.OrderBy(b => b.Id).ToArray());

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class MutableUser
    {
        public required string TradingUserId { get; init; }
        public required string DisplayName { get; set; }
        public required string FirmId { get; set; }
        public required string Status { get; set; }
        public required string Role { get; set; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; set; }
        public required long RowVersion { get; set; }
        public List<ExternalIdentityBinding> Bindings { get; } = new();
    }
}
