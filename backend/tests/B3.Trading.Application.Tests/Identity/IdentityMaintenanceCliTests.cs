using System.Text.Json;
using B3.Trading.Application.Identity;
using B3.Trading.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Identity;

public sealed class IdentityMaintenanceCliTests
{
    [Fact]
    public async Task Backup_WhileWriterTransactionIsActive_ProducesValidatedArtifactAndJsonMetadata()
    {
        using var workspace = TestWorkspace.Create(nameof(Backup_WhileWriterTransactionIsActive_ProducesValidatedArtifactAndJsonMetadata));
        var database = Path.Combine(workspace.Path, "users.db");
        var destination = Path.Combine(workspace.Path, "backup", "users.db");
        var directory = NewDirectory(database);
        await directory.InitializeAsync();
        await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("alice", "Alice", "FIRM01", TradingUserDirectoryConstants.RoleUser),
        });

        await using var writer = Open(database);
        await ExecuteAsync(writer, "BEGIN IMMEDIATE;");
        await ExecuteAsync(writer, "UPDATE users SET display_name = 'Uncommitted' WHERE trading_user_id = 'alice';");

        var backupOut = new StringWriter();
        var backupError = new StringWriter();
        var backupExit = await IdentityMaintenanceCli.RunAsync(
            new[] { "backup", "--database", database, "--destination", destination },
            backupOut,
            backupError);

        await ExecuteAsync(writer, "ROLLBACK;");

        Assert.Equal(0, backupExit);
        Assert.Equal(string.Empty, backupError.ToString());
        Assert.True(File.Exists(destination));
        using (var metadata = JsonDocument.Parse(backupOut.ToString()))
        {
            Assert.Equal("backup", metadata.RootElement.GetProperty("command").GetString());
            Assert.Equal(Path.GetFullPath(destination), metadata.RootElement.GetProperty("destination").GetString());
            Assert.Equal(SqliteTradingUserDirectory.CurrentSchemaVersion, metadata.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(metadata.RootElement.TryGetProperty("createdAtUtc", out _));
        }

        var validateOut = new StringWriter();
        var validateExit = await IdentityMaintenanceCli.RunAsync(
            new[] { "validate", "--database", destination },
            validateOut,
            new StringWriter());

        Assert.Equal(0, validateExit);
        using var validation = JsonDocument.Parse(validateOut.ToString());
        Assert.Equal("validate", validation.RootElement.GetProperty("command").GetString());
        Assert.Equal(SqliteTradingUserDirectory.CurrentSchemaVersion, validation.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Path.GetFullPath(destination), validation.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public async Task Validate_MissingDatabase_FailsWithoutCreatingIt()
    {
        using var workspace = TestWorkspace.Create(nameof(Validate_MissingDatabase_FailsWithoutCreatingIt));
        var database = Path.Combine(workspace.Path, "missing", "users.db");

        var exitCode = await IdentityMaintenanceCli.RunAsync(
            new[] { "validate", "--database", database },
            new StringWriter(),
            new StringWriter());

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(database));
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task Validate_CorruptDatabase_FailsWithoutReplacingIt()
    {
        using var workspace = TestWorkspace.Create(nameof(Validate_CorruptDatabase_FailsWithoutReplacingIt));
        var database = Path.Combine(workspace.Path, "users.db");
        var original = "not-a-sqlite-database"u8.ToArray();
        await File.WriteAllBytesAsync(database, original);

        var exitCode = await IdentityMaintenanceCli.RunAsync(
            new[] { "validate", "--database", database },
            new StringWriter(),
            new StringWriter());

        Assert.Equal(1, exitCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(database));
    }

    [Fact]
    public async Task Validate_FutureSchema_FailsWithoutMigratingIt()
    {
        using var workspace = TestWorkspace.Create(nameof(Validate_FutureSchema_FailsWithoutMigratingIt));
        var database = Path.Combine(workspace.Path, "users.db");
        await NewDirectory(database).InitializeAsync();
        await using (var connection = Open(database))
            await ExecuteAsync(connection, "INSERT INTO schema_migrations (version, applied_at) VALUES (999, '2026-07-16T00:00:00Z');");

        var exitCode = await IdentityMaintenanceCli.RunAsync(
            new[] { "validate", "--database", database },
            new StringWriter(),
            new StringWriter());

        Assert.Equal(1, exitCode);
        await using var verification = Open(database);
        Assert.Equal(999L, (long)(await ScalarAsync(verification, "SELECT MAX(version) FROM schema_migrations;"))!);
    }

    [Fact]
    public async Task Backup_MissingSource_FailsWithoutCreatingSourceOrDestination()
    {
        using var workspace = TestWorkspace.Create(nameof(Backup_MissingSource_FailsWithoutCreatingSourceOrDestination));
        var database = Path.Combine(workspace.Path, "missing", "users.db");
        var destination = Path.Combine(workspace.Path, "backup", "users.db");

        var exitCode = await IdentityMaintenanceCli.RunAsync(
            new[] { "backup", "--database", database, "--destination", destination },
            new StringWriter(),
            new StringWriter());

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Backup_EmptySource_FailsWithoutInitializingOrReplacingIt()
    {
        using var workspace = TestWorkspace.Create(nameof(Backup_EmptySource_FailsWithoutInitializingOrReplacingIt));
        var database = Path.Combine(workspace.Path, "users.db");
        var destination = Path.Combine(workspace.Path, "backup.db");
        await File.WriteAllBytesAsync(database, Array.Empty<byte>());

        var exitCode = await IdentityMaintenanceCli.RunAsync(
            new[] { "backup", "--database", database, "--destination", destination },
            new StringWriter(),
            new StringWriter());

        Assert.Equal(1, exitCode);
        Assert.Equal(0, new FileInfo(database).Length);
        Assert.False(File.Exists($"{database}-wal"));
        Assert.False(File.Exists($"{database}-shm"));
        Assert.False(File.Exists(destination));
    }

    private static SqliteTradingUserDirectory NewDirectory(string path) =>
        new(
            Options.Create(new IdentityDirectoryOptions
            {
                Provider = IdentityDirectoryProviders.Sqlite,
                Path = path,
                ImportLegacyUsersOnStartup = false,
                ExpectedWriterCount = 1,
                BusyTimeoutMilliseconds = 5_000,
            }),
            NullLogger<SqliteTradingUserDirectory>.Instance);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
