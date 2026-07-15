using System.Data;
using B3.Trading.Application.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure.Identity;

public sealed class SqliteTradingUserDirectory : ITradingUserDirectory
{
    public const int CurrentSchemaVersion = 1;

    private readonly IdentityDirectoryOptions _options;
    private readonly ILogger<SqliteTradingUserDirectory> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile TradingUserDirectoryHealth _lastHealth;

    public SqliteTradingUserDirectory(
        IOptions<IdentityDirectoryOptions> options,
        ILogger<SqliteTradingUserDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrWhiteSpace(_options.Path))
            throw new InvalidOperationException($"{IdentityDirectoryOptions.SectionName}:Path is required for the SQLite identity directory.");
        _lastHealth = new TradingUserDirectoryHealth(false, ProviderName, _options.Path, null, "not_initialized");
    }

    public string ProviderName => IdentityDirectoryProviders.Sqlite;
    public string? StorePath => _options.Path;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_options.ExpectedWriterCount != 1)
            throw new InvalidOperationException($"{IdentityDirectoryOptions.SectionName}:ExpectedWriterCount must be 1 for SQLite/RWO.");
        if (!_options.MigrateOnStartup)
            throw new InvalidOperationException($"{IdentityDirectoryOptions.SectionName}:MigrateOnStartup=false is not supported for SQLite.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_options.Path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var connection = OpenConnection();
            await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", ct: ct).ConfigureAwait(false);
            try
            {
                await ApplyMigrationsAsync(connection, ct).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, "COMMIT;", ct: ct).ConfigureAwait(false);
            }
            catch
            {
                await ExecuteNonQueryAsync(connection, "ROLLBACK;", ct: CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var version = await RunIntegrityChecksAsync(connection, ct).ConfigureAwait(false);
            _lastHealth = new TradingUserDirectoryHealth(true, ProviderName, _options.Path, version, null);
            _logger.LogInformation("SQLite identity directory ready at {Path} schema_version={SchemaVersion}.", _options.Path, version);
        }
        catch (Exception ex) when (ex is not TradingUserDirectoryUnsupportedSchemaException)
        {
            _lastHealth = new TradingUserDirectoryHealth(false, ProviderName, _options.Path, null, "startup_failed");
            throw new TradingUserDirectoryUnavailableException("SQLite identity directory failed startup checks.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<TradingUserDirectoryHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = OpenConnection(readOnly: true);
            var version = await RunIntegrityChecksAsync(connection, ct).ConfigureAwait(false);
            _lastHealth = new TradingUserDirectoryHealth(true, ProviderName, _options.Path, version, null);
        }
        catch (Exception ex)
        {
            _lastHealth = new TradingUserDirectoryHealth(false, ProviderName, _options.Path, _lastHealth.SchemaVersion, ex.GetType().Name);
        }

        return _lastHealth;
    }

    public async Task<TradingUser?> GetUserAsync(string tradingUserId, CancellationToken ct = default)
    {
        InMemoryTradingUserDirectory.ValidateTradingUserId(tradingUserId);
        await using var connection = OpenConnection(readOnly: true);
        return await LoadUserAsync(connection, tradingUserId, ct).ConfigureAwait(false);
    }

    public async Task<TradingUser?> ResolveExternalIdentityAsync(string issuer, string subject, CancellationToken ct = default)
    {
        InMemoryTradingUserDirectory.ValidateBinding(new ExternalIdentityBindingRequest(issuer, subject));
        await using var connection = OpenConnection(readOnly: true);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT trading_user_id
            FROM external_identities
            WHERE issuer = $issuer COLLATE BINARY
              AND subject = $subject COLLATE BINARY;
            """;
        cmd.Parameters.AddWithValue("$issuer", issuer);
        cmd.Parameters.AddWithValue("$subject", subject);
        var id = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return id is null ? null : await LoadUserAsync(connection, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TradingUser>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection(readOnly: true);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT trading_user_id FROM users ORDER BY trading_user_id COLLATE BINARY;";
        var ids = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                ids.Add(reader.GetString(0));
        }

        var users = new List<TradingUser>(ids.Count);
        foreach (var id in ids)
        {
            var user = await LoadUserAsync(connection, id, ct).ConfigureAwait(false);
            if (user is not null)
                users.Add(user);
        }

        return users;
    }

    public async Task<int> ImportLegacyUsersAsync(IReadOnlyCollection<LegacyTradingUserImport> users, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        InMemoryTradingUserDirectory.ValidateLegacyBatch(users);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await RejectExistingOwnerNamespaceCollisionsAsync(connection, tx, users, ct).ConfigureAwait(false);
            var inserted = 0;
            foreach (var user in users)
            {
                if (await UserExistsAsync(connection, tx, user.TradingUserId, ct).ConfigureAwait(false))
                    continue;

                var now = FormatTimestamp(DateTimeOffset.UtcNow);
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = (SqliteTransaction)tx;
                    insert.CommandText = """
                        INSERT INTO users (trading_user_id, display_name, firm_id, status, created_at, updated_at, row_version)
                        VALUES ($id, $display, $firm, 'active', $created, $updated, 1);
                        """;
                    insert.Parameters.AddWithValue("$id", user.TradingUserId);
                    insert.Parameters.AddWithValue("$display", user.DisplayName);
                    insert.Parameters.AddWithValue("$firm", user.FirmId);
                    insert.Parameters.AddWithValue("$created", now);
                    insert.Parameters.AddWithValue("$updated", now);
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var role = connection.CreateCommand())
                {
                    role.Transaction = (SqliteTransaction)tx;
                    role.CommandText = "INSERT INTO user_roles (trading_user_id, role) VALUES ($id, $role);";
                    role.Parameters.AddWithValue("$id", user.TradingUserId);
                    role.Parameters.AddWithValue("$role", user.Role);
                    await role.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                inserted++;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return inserted;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new TradingUserDirectoryConflictException("Legacy identity import violates directory constraints.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ExternalIdentityBinding> BindExternalIdentityAsync(
        string tradingUserId,
        ExternalIdentityBindingRequest binding,
        long expectedRowVersion,
        CancellationToken ct = default)
    {
        InMemoryTradingUserDirectory.ValidateTradingUserId(tradingUserId);
        InMemoryTradingUserDirectory.ValidateBinding(binding);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await AdvanceRowVersionAsync(connection, tx, tradingUserId, expectedRowVersion, ct).ConfigureAwait(false);
            var now = FormatTimestamp(DateTimeOffset.UtcNow);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO external_identities (issuer, subject, trading_user_id, tenant_id, object_id, created_at)
                VALUES ($issuer, $subject, $tradingUserId, $tenantId, $objectId, $createdAt)
                RETURNING id, issuer, subject, trading_user_id, tenant_id, object_id, created_at;
                """;
            cmd.Parameters.AddWithValue("$issuer", binding.Issuer);
            cmd.Parameters.AddWithValue("$subject", binding.Subject);
            cmd.Parameters.AddWithValue("$tradingUserId", tradingUserId);
            cmd.Parameters.AddWithValue("$tenantId", DbValue(binding.TenantId));
            cmd.Parameters.AddWithValue("$objectId", DbValue(binding.ObjectId));
            cmd.Parameters.AddWithValue("$createdAt", now);

            ExternalIdentityBinding row;
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new TradingUserDirectoryUnavailableException("SQLite did not return the inserted external identity.");
                row = ReadBinding(reader);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return row;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new TradingUserDirectoryConflictException("External identity binding conflicts with an existing row.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task UnbindExternalIdentityAsync(string tradingUserId, long bindingId, long expectedRowVersion, CancellationToken ct = default)
    {
        InMemoryTradingUserDirectory.ValidateTradingUserId(tradingUserId);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await AdvanceRowVersionAsync(connection, tx, tradingUserId, expectedRowVersion, ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "DELETE FROM external_identities WHERE id = $id AND trading_user_id = $tradingUserId;";
            cmd.Parameters.AddWithValue("$id", bindingId);
            cmd.Parameters.AddWithValue("$tradingUserId", tradingUserId);
            var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (deleted != 1)
                throw new TradingUserDirectoryConflictException("External identity binding does not exist for the user.");
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetStatusAsync(string tradingUserId, string status, long expectedRowVersion, CancellationToken ct = default)
    {
        InMemoryTradingUserDirectory.ValidateTradingUserId(tradingUserId);
        if (!TradingUserDirectoryConstants.IsValidStatus(status))
            throw new TradingUserDirectoryValidationException("Invalid trading user status.");

        await MutateUserAsync(tradingUserId, expectedRowVersion, "status = $value", ("$value", status), ct).ConfigureAwait(false);
    }

    public async Task SetFirmAndRoleAsync(string tradingUserId, string firmId, string role, long expectedRowVersion, CancellationToken ct = default)
    {
        InMemoryTradingUserDirectory.ValidateTradingUserId(tradingUserId);
        InMemoryTradingUserDirectory.ValidateFirmAndRole(firmId, role);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await AdvanceRowVersionAsync(connection, tx, tradingUserId, expectedRowVersion, ct).ConfigureAwait(false);

            await using var roleCmd = connection.CreateCommand();
            roleCmd.Transaction = (SqliteTransaction)tx;
            roleCmd.CommandText = "UPDATE user_roles SET role = $role WHERE trading_user_id = $id;";
            roleCmd.Parameters.AddWithValue("$role", role);
            roleCmd.Parameters.AddWithValue("$id", tradingUserId);
            if (await roleCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new TradingUserDirectoryConflictException("Trading user role row is missing.");

            await using var firmCmd = connection.CreateCommand();
            firmCmd.Transaction = (SqliteTransaction)tx;
            firmCmd.CommandText = "UPDATE users SET firm_id = $firm WHERE trading_user_id = $id;";
            firmCmd.Parameters.AddWithValue("$firm", firmId);
            firmCmd.Parameters.AddWithValue("$id", tradingUserId);
            await firmCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<TradingUserDirectoryBackup> CreateBackupAsync(string destinationPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new TradingUserDirectoryValidationException("Backup destination path is required.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var source = OpenConnection();
            await ExecuteNonQueryAsync(source, "PRAGMA wal_checkpoint(PASSIVE);", ct: ct).ConfigureAwait(false);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
                Pooling = false,
            };
            await using var destination = new SqliteConnection(builder.ToString());
            await destination.OpenAsync(ct).ConfigureAwait(false);
            source.BackupDatabase(destination);
            await ConfigureConnectionAsync(destination, ct).ConfigureAwait(false);
            var version = await RunIntegrityChecksAsync(destination, ct).ConfigureAwait(false);
            return new TradingUserDirectoryBackup(destinationPath, version, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not TradingUserDirectoryException)
        {
            throw new TradingUserDirectoryUnavailableException("SQLite identity backup failed.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task MutateUserAsync(
        string tradingUserId,
        long expectedRowVersion,
        string setClause,
        (string Name, object Value) parameter,
        CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await AdvanceRowVersionAsync(connection, tx, tradingUserId, expectedRowVersion, setClause, parameter, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var hasMigrations = await TableExistsAsync(connection, "schema_migrations", ct).ConfigureAwait(false);
        if (hasMigrations)
        {
            var max = await CurrentVersionAsync(connection, ct).ConfigureAwait(false);
            if (max > CurrentSchemaVersion)
                throw new TradingUserDirectoryUnsupportedSchemaException(max, CurrentSchemaVersion);
            if (max == CurrentSchemaVersion)
                return;

            throw new TradingUserDirectoryUnavailableException("Identity schema_migrations exists without a supported version.");
        }

        if (await AnyManagedDataTableExistsAsync(connection, ct).ConfigureAwait(false))
            throw new TradingUserDirectoryUnavailableException("Unversioned identity database contains pre-existing managed tables.");

        await ExecuteNonQueryAsync(connection, Migration001, ct: ct).ConfigureAwait(false);
    }

    private async Task<int> RunIntegrityChecksAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, "schema_migrations", ct).ConfigureAwait(false))
            throw new TradingUserDirectoryUnavailableException("Identity schema_migrations table is missing.");
        var version = await CurrentVersionAsync(connection, ct).ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
            throw new TradingUserDirectoryUnsupportedSchemaException(version, CurrentSchemaVersion);
        if (version != CurrentSchemaVersion)
            throw new TradingUserDirectoryUnavailableException("Identity directory schema version is missing or unsupported.");

        await VerifyManagedSchemaAsync(connection, ct).ConfigureAwait(false);

        var quickCheck = (string?)await ScalarAsync(connection, "PRAGMA quick_check;", ct).ConfigureAwait(false);
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            throw new TradingUserDirectoryUnavailableException("SQLite quick_check failed.");

        var foreignKeyFailures = Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", ct).ConfigureAwait(false));
        if (foreignKeyFailures != 0)
            throw new TradingUserDirectoryUnavailableException("SQLite foreign_key_check failed.");

        var invalidActiveUsers = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM users u
            LEFT JOIN user_roles r ON r.trading_user_id = u.trading_user_id
            WHERE u.status = 'active'
              AND (length(u.firm_id) = 0 OR r.role IS NULL);
            """, ct).ConfigureAwait(false));
        if (invalidActiveUsers != 0)
            throw new TradingUserDirectoryUnavailableException("Identity directory contains active users without exactly one firm/role.");

        await RejectStoredOwnerNamespaceCollisionsAsync(connection, ct).ConfigureAwait(false);

        return version;
    }

    private SqliteConnection OpenConnection(bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.Path!,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            ConfigureConnectionAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", ct: ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, $"PRAGMA busy_timeout = {Math.Max(1, _options.BusyTimeoutMilliseconds)};", ct: ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", ct: ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous = FULL;", ct: ct).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name, CancellationToken ct)
    {
        var result = await ScalarAsync(connection,
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;",
            ct,
            ("$name", name)).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<bool> AnyManagedDataTableExistsAsync(SqliteConnection connection, CancellationToken ct)
    {
        foreach (var table in new[] { "users", "external_identities", "user_roles" })
        {
            if (await TableExistsAsync(connection, table, ct).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private static async Task<int> CurrentVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        var result = await ScalarAsync(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;", ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private static async Task VerifyManagedSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await RequireColumnsAsync(connection, "schema_migrations", ct,
            new RequiredColumn("version", "INTEGER", NotNull: false, PrimaryKey: true),
            new RequiredColumn("applied_at", "TEXT", NotNull: true, PrimaryKey: false)).ConfigureAwait(false);
        await RequireColumnsAsync(connection, "users", ct,
            new RequiredColumn("trading_user_id", "TEXT", NotNull: true, PrimaryKey: true),
            new RequiredColumn("display_name", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("firm_id", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("status", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("created_at", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("updated_at", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("row_version", "INTEGER", NotNull: true, PrimaryKey: false)).ConfigureAwait(false);
        await RequireColumnsAsync(connection, "external_identities", ct,
            new RequiredColumn("id", "INTEGER", NotNull: false, PrimaryKey: true),
            new RequiredColumn("issuer", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("subject", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("trading_user_id", "TEXT", NotNull: true, PrimaryKey: false),
            new RequiredColumn("tenant_id", "TEXT", NotNull: false, PrimaryKey: false),
            new RequiredColumn("object_id", "TEXT", NotNull: false, PrimaryKey: false),
            new RequiredColumn("created_at", "TEXT", NotNull: true, PrimaryKey: false)).ConfigureAwait(false);
        await RequireColumnsAsync(connection, "user_roles", ct,
            new RequiredColumn("trading_user_id", "TEXT", NotNull: true, PrimaryKey: true),
            new RequiredColumn("role", "TEXT", NotNull: true, PrimaryKey: false)).ConfigureAwait(false);

        if (!await HasUniqueIndexAsync(connection, "external_identities", ct, "issuer", "subject").ConfigureAwait(false))
            throw new TradingUserDirectoryUnavailableException("Identity schema is missing UNIQUE(issuer, subject).");
        if (!await HasForeignKeyAsync(connection, "external_identities", "users", "trading_user_id", "trading_user_id", "RESTRICT", ct).ConfigureAwait(false))
            throw new TradingUserDirectoryUnavailableException("Identity schema external_identities foreign key is invalid.");
        if (!await HasForeignKeyAsync(connection, "user_roles", "users", "trading_user_id", "trading_user_id", "CASCADE", ct).ConfigureAwait(false))
            throw new TradingUserDirectoryUnavailableException("Identity schema user_roles foreign key is invalid.");
    }

    private static async Task RequireColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken ct,
        params RequiredColumn[] required)
    {
        if (!await TableExistsAsync(connection, table, ct).ConfigureAwait(false))
            throw new TradingUserDirectoryUnavailableException($"Identity schema table '{table}' is missing.");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name, upper(type), \"notnull\", pk FROM pragma_table_info('{table}');";
        var columns = new Dictionary<string, (string Type, bool NotNull, bool PrimaryKey)>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns[reader.GetString(0)] = (
                    reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    reader.GetInt32(3) != 0);
            }
        }

        foreach (var column in required)
        {
            if (!columns.TryGetValue(column.Name, out var actual)
                || !string.Equals(actual.Type, column.Type, StringComparison.OrdinalIgnoreCase)
                || actual.NotNull != column.NotNull
                || actual.PrimaryKey != column.PrimaryKey)
            {
                throw new TradingUserDirectoryUnavailableException($"Identity schema column '{table}.{column.Name}' is invalid.");
            }
        }
    }

    private static async Task<bool> HasUniqueIndexAsync(
        SqliteConnection connection,
        string table,
        CancellationToken ct,
        params string[] expectedColumns)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_index_list('{table}') WHERE \"unique\" = 1;";
        var indexNames = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                indexNames.Add(reader.GetString(0));
        }

        foreach (var indexName in indexNames)
        {
            await using var info = connection.CreateCommand();
            info.CommandText = $"SELECT name FROM pragma_index_info('{indexName.Replace("'", "''")}') ORDER BY seqno;";
            var columns = new List<string>();
            await using var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columns.Add(reader.GetString(0));

            if (columns.SequenceEqual(expectedColumns, StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    private static async Task<bool> HasForeignKeyAsync(
        SqliteConnection connection,
        string table,
        string targetTable,
        string fromColumn,
        string toColumn,
        string onDelete,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT 1
            FROM pragma_foreign_key_list('{table}')
            WHERE "table" = $targetTable
              AND "from" = $fromColumn
              AND "to" = $toColumn
              AND upper(on_delete) = $onDelete;
            """;
        cmd.Parameters.AddWithValue("$targetTable", targetTable);
        cmd.Parameters.AddWithValue("$fromColumn", fromColumn);
        cmd.Parameters.AddWithValue("$toColumn", toColumn);
        cmd.Parameters.AddWithValue("$onDelete", onDelete);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private async Task<TradingUser?> LoadUserAsync(SqliteConnection connection, string tradingUserId, CancellationToken ct)
    {
        await using var userCmd = connection.CreateCommand();
        userCmd.CommandText = """
            SELECT u.trading_user_id, u.display_name, u.firm_id, u.status, r.role,
                   u.row_version, u.created_at, u.updated_at
            FROM users u
            LEFT JOIN user_roles r ON r.trading_user_id = u.trading_user_id
            WHERE u.trading_user_id = $id COLLATE BINARY;
            """;
        userCmd.Parameters.AddWithValue("$id", tradingUserId);
        await using var reader = await userCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var id = reader.GetString(0);
        var bindings = await LoadBindingsAsync(connection, id, ct).ConfigureAwait(false);
        return new TradingUser(
            id,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetInt64(5),
            ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)),
            bindings);
    }

    private static async Task<IReadOnlyList<ExternalIdentityBinding>> LoadBindingsAsync(SqliteConnection connection, string tradingUserId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, issuer, subject, trading_user_id, tenant_id, object_id, created_at
            FROM external_identities
            WHERE trading_user_id = $id
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$id", tradingUserId);
        var bindings = new List<ExternalIdentityBinding>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            bindings.Add(ReadBinding(reader));
        return bindings;
    }

    private static ExternalIdentityBinding ReadBinding(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ParseTimestamp(reader.GetString(6)));

    private static async Task<bool> UserExistsAsync(SqliteConnection connection, System.Data.Common.DbTransaction tx, string tradingUserId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "SELECT 1 FROM users WHERE trading_user_id = $id COLLATE BINARY;";
        cmd.Parameters.AddWithValue("$id", tradingUserId);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async Task RejectExistingOwnerNamespaceCollisionsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction tx,
        IEnumerable<LegacyTradingUserImport> imports,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "SELECT trading_user_id FROM users;";
        var existing = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(0));
        }

        foreach (var import in imports)
        {
            if (existing.Any(id =>
                string.Equals(
                    InMemoryTradingUserDirectory.ProjectOwnerId(id),
                    InMemoryTradingUserDirectory.ProjectOwnerId(import.TradingUserId),
                    StringComparison.Ordinal)
                && !string.Equals(id, import.TradingUserId, StringComparison.Ordinal)))
            {
                throw new TradingUserDirectoryValidationException("Legacy import would create an end-client owner namespace collision.");
            }
        }
    }

    private static async Task RejectStoredOwnerNamespaceCollisionsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT trading_user_id FROM users ORDER BY trading_user_id COLLATE BINARY;";
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var ownerId = InMemoryTradingUserDirectory.ProjectOwnerId(id);
            if (seen.TryGetValue(ownerId, out var existing)
                && !string.Equals(existing, id, StringComparison.Ordinal))
            {
                throw new TradingUserDirectoryUnavailableException("Identity directory contains end-client owner namespace collisions.");
            }

            seen[ownerId] = id;
        }
    }

    private static async Task AdvanceRowVersionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction tx,
        string tradingUserId,
        long expectedRowVersion,
        CancellationToken ct,
        string? additionalSet = null)
    {
        await AdvanceRowVersionAsync(connection, tx, tradingUserId, expectedRowVersion, additionalSet, default, ct).ConfigureAwait(false);
    }

    private static async Task AdvanceRowVersionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction tx,
        string tradingUserId,
        long expectedRowVersion,
        string? additionalSet,
        (string Name, object Value) parameter,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = $"""
            UPDATE users
            SET row_version = row_version + 1,
                updated_at = $updatedAt{(additionalSet is null ? string.Empty : ",\n                " + additionalSet)}
            WHERE trading_user_id = $id COLLATE BINARY
              AND row_version = $expectedRowVersion;
            """;
        cmd.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", tradingUserId);
        cmd.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);
        if (parameter.Name is not null)
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (updated != 1)
        {
            if (await UserExistsAsync(connection, tx, tradingUserId, ct).ConfigureAwait(false))
                throw new TradingUserDirectoryConcurrencyException("Trading user row version is stale.");
            throw new TradingUserDirectoryConflictException("Trading user does not exist.");
        }
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? tx = null,
        CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private sealed record RequiredColumn(string Name, string Type, bool NotNull, bool PrimaryKey);

    private const string Migration001 = """
        CREATE TABLE schema_migrations (
            version       INTEGER PRIMARY KEY,
            applied_at    TEXT NOT NULL
        );

        CREATE TABLE users (
            trading_user_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY
                CHECK (length(trading_user_id) BETWEEN 1 AND 64),
            display_name    TEXT NOT NULL CHECK (length(display_name) > 0),
            firm_id         TEXT NOT NULL CHECK (length(firm_id) > 0),
            status          TEXT NOT NULL CHECK (status IN ('active', 'disabled')),
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            row_version     INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE external_identities (
            id              INTEGER PRIMARY KEY,
            issuer          TEXT NOT NULL COLLATE BINARY CHECK (length(issuer) > 0),
            subject         TEXT NOT NULL COLLATE BINARY CHECK (length(subject) > 0),
            trading_user_id TEXT NOT NULL CHECK (length(trading_user_id) BETWEEN 1 AND 64),
            tenant_id       TEXT NULL,
            object_id       TEXT NULL,
            created_at      TEXT NOT NULL,
            UNIQUE (issuer, subject),
            FOREIGN KEY (trading_user_id)
                REFERENCES users(trading_user_id) ON DELETE RESTRICT
        );

        CREATE TABLE user_roles (
            trading_user_id TEXT NOT NULL PRIMARY KEY
                CHECK (length(trading_user_id) BETWEEN 1 AND 64),
            role            TEXT NOT NULL
                CHECK (role IN ('user', 'compliance', 'admin')),
            FOREIGN KEY (trading_user_id)
                REFERENCES users(trading_user_id) ON DELETE CASCADE
        );

        INSERT OR IGNORE INTO schema_migrations (version, applied_at)
        VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """;
}
