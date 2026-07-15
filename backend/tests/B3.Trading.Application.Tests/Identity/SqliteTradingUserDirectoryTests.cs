using B3.Trading.Application.Identity;
using B3.Trading.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Identity;

public sealed class SqliteTradingUserDirectoryTests
{
    [Fact]
    public async Task EmptyDatabase_MigratesWithRequiredPragmas_AndPersistsAcrossRestart()
    {
        using var workspace = TestWorkspace.Create(nameof(EmptyDatabase_MigratesWithRequiredPragmas_AndPersistsAcrossRestart));
        var db = System.IO.Path.Combine(workspace.Path, "identity", "users.db");
        var directory = NewDirectory(db);

        await directory.InitializeAsync();
        await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("Alice.Raw", "Alice.Raw", "FIRM01", TradingUserDirectoryConstants.RoleAdmin),
        });

        var restarted = NewDirectory(db);
        await restarted.InitializeAsync();
        var user = await restarted.GetUserAsync("Alice.Raw");

        Assert.NotNull(user);
        Assert.Equal("Alice.Raw", user.TradingUserId);
        await using var connection = Open(db);
        Assert.Equal("wal", (string?)await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(connection, "PRAGMA synchronous;")));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "PRAGMA foreign_keys;")));
        Assert.Equal(SqliteTradingUserDirectory.CurrentSchemaVersion, Convert.ToInt32(await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;")));
    }

    [Fact]
    public async Task FutureSchemaVersion_FailsClosed()
    {
        using var workspace = TestWorkspace.Create(nameof(FutureSchemaVersion_FailsClosed));
        var db = System.IO.Path.Combine(workspace.Path, "users.db");
        Directory.CreateDirectory(workspace.Path);
        await using (var connection = Open(db))
        {
            await ExecuteAsync(connection, "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
            await ExecuteAsync(connection, "INSERT INTO schema_migrations (version, applied_at) VALUES (999, '2026-01-01T00:00:00Z');");
        }

        await Assert.ThrowsAsync<TradingUserDirectoryUnsupportedSchemaException>(() =>
            NewDirectory(db).InitializeAsync());
    }

    [Fact]
    public async Task CorruptDatabase_FailsClosed()
    {
        using var workspace = TestWorkspace.Create(nameof(CorruptDatabase_FailsClosed));
        var db = System.IO.Path.Combine(workspace.Path, "users.db");
        Directory.CreateDirectory(workspace.Path);
        await File.WriteAllTextAsync(db, "not a sqlite database");

        await Assert.ThrowsAsync<TradingUserDirectoryUnavailableException>(() =>
            NewDirectory(db).InitializeAsync());
    }

    [Fact]
    public async Task OnlineBackup_RestoresToValidOfflineDirectory()
    {
        using var workspace = TestWorkspace.Create(nameof(OnlineBackup_RestoresToValidOfflineDirectory));
        var db = System.IO.Path.Combine(workspace.Path, "users.db");
        var backup = System.IO.Path.Combine(workspace.Path, "backup.db");
        var directory = NewDirectory(db);
        await directory.InitializeAsync();
        await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("alice", "alice", "FIRM01", TradingUserDirectoryConstants.RoleUser),
            new LegacyTradingUserImport("admin", "admin", "FIRM01", TradingUserDirectoryConstants.RoleAdmin),
        });

        var admin = await directory.GetUserAsync("admin");
        Assert.NotNull(admin);
        await directory.BindExternalIdentityAsync(
            "admin",
            new ExternalIdentityBindingRequest("https://issuer.example/v2.0", "admin-subject"),
            admin.RowVersion);
        await directory.SetStatusAsync("alice", TradingUserDirectoryConstants.StatusDisabled, expectedRowVersion: 1);

        var result = await directory.CreateBackupAsync(backup);
        Assert.Equal(SqliteTradingUserDirectory.CurrentSchemaVersion, result.SchemaVersion);

        var restored = NewDirectory(backup);
        await restored.InitializeAsync();
        var restoredAdmin = await restored.ResolveExternalIdentityAsync("https://issuer.example/v2.0", "admin-subject");
        var restoredAlice = await restored.GetUserAsync("alice");

        Assert.NotNull(restoredAdmin);
        Assert.Equal(TradingUserDirectoryConstants.RoleAdmin, restoredAdmin.Role);
        Assert.NotNull(restoredAlice);
        Assert.Equal(TradingUserDirectoryConstants.StatusDisabled, restoredAlice.Status);
    }

    [Fact]
    public async Task ConcurrentBindings_AllowOnlyOneWinnerForSameExternalIdentity()
    {
        using var workspace = TestWorkspace.Create(nameof(ConcurrentBindings_AllowOnlyOneWinnerForSameExternalIdentity));
        var directory = NewDirectory(System.IO.Path.Combine(workspace.Path, "users.db"));
        await directory.InitializeAsync();
        await directory.ImportLegacyUsersAsync(Enumerable.Range(0, 8)
            .Select(i => new LegacyTradingUserImport($"user{i}", $"user{i}", "FIRM01", TradingUserDirectoryConstants.RoleUser))
            .ToArray());

        var attempts = Enumerable.Range(0, 8).Select(async i =>
        {
            try
            {
                await directory.BindExternalIdentityAsync(
                    $"user{i}",
                    new ExternalIdentityBindingRequest("issuer", "same-subject"),
                    expectedRowVersion: 1);
                return true;
            }
            catch (TradingUserDirectoryConflictException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(x => x));
        Assert.NotNull(await directory.ResolveExternalIdentityAsync("issuer", "same-subject"));
    }

    private static SqliteTradingUserDirectory NewDirectory(string path) =>
        new(
            Options.Create(new IdentityDirectoryOptions
            {
                Provider = IdentityDirectoryProviders.Sqlite,
                Path = path,
                BusyTimeoutMilliseconds = 5000,
            }),
            NullLogger<SqliteTradingUserDirectory>.Instance);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
