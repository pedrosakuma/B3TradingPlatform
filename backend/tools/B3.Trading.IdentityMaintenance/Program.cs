using System.Text.Json;
using B3.Trading.Application.Identity;
using B3.Trading.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

return await IdentityMaintenanceCli.RunAsync(args);

public static class IdentityMaintenanceCli
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(stderr);
            return args.Length == 0 ? 2 : 0;
        }

        var parsed = Parse(args.Skip(1).ToArray());
        return args[0] switch
        {
            "backup" => await RunBackupAsync(parsed, stdout, stderr).ConfigureAwait(false),
            "validate" => await RunValidateAsync(parsed, stdout, stderr).ConfigureAwait(false),
            "recover-admin" => await RunRecoverAdminAsync(parsed, stdout, stderr).ConfigureAwait(false),
            _ => UnknownCommand(stderr),
        };
    }

    private static async Task<int> RunBackupAsync(
        IReadOnlyDictionary<string, string> parsed,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (!TryRequire(parsed, stderr, "database", "destination"))
            return 2;

        var database = parsed["database"];
        var destination = parsed["destination"];
        if (!File.Exists(database))
            return await WriteFailureAsync(stderr, "Identity database does not exist.").ConfigureAwait(false);

        try
        {
            var result = await CreateDirectory(database).CreateBackupAsync(destination).ConfigureAwait(false);
            await WriteJsonAsync(stdout, new
            {
                command = "backup",
                destination = result.Path,
                schemaVersion = result.SchemaVersion,
                createdAtUtc = result.CreatedAt.ToUniversalTime(),
            }).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            return await WriteFailureAsync(stderr, ex.Message).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunValidateAsync(
        IReadOnlyDictionary<string, string> parsed,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (!TryRequire(parsed, stderr, "database"))
            return 2;

        var database = parsed["database"];
        if (!File.Exists(database))
            return await WriteFailureAsync(stderr, "Identity database does not exist.").ConfigureAwait(false);

        try
        {
            var result = await CreateDirectory(database).ValidateOfflineAsync().ConfigureAwait(false);
            await WriteJsonAsync(stdout, new
            {
                command = "validate",
                database = result.Path,
                schemaVersion = result.SchemaVersion,
                validatedAtUtc = result.ValidatedAt.ToUniversalTime(),
            }).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            return await WriteFailureAsync(stderr, ex.Message).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunRecoverAdminAsync(
        IReadOnlyDictionary<string, string> parsed,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (!TryRequire(
            parsed,
            stderr,
            "database",
            "trading-user-id",
            "display-name",
            "firm-id",
            "operator",
            "change-ticket"))
        {
            return 2;
        }

        var database = parsed["database"];
        try
        {
            if (File.Exists(database))
                await RefuseActiveWriterAsync(database).ConfigureAwait(false);

            var directory = CreateDirectory(database);
            await directory.InitializeAsync().ConfigureAwait(false);
            await RefuseActiveWriterAsync(database).ConfigureAwait(false);
            var result = await directory.EnsureRecoveryAdminAsync(new RecoveryAdminRequest(
                parsed["trading-user-id"],
                parsed["display-name"],
                parsed["firm-id"],
                parsed["operator"],
                parsed["change-ticket"])).ConfigureAwait(false);

            await stdout.WriteLineAsync(
                $"recovery_admin={(result.Created ? "created" : "enabled")} trading_user_id={result.User.TradingUserId} row_version={result.User.RowVersion} maintenance_event_id={result.MaintenanceEventId}").ConfigureAwait(false);
            await stdout.WriteLineAsync("password_material=legacy_secret_config_not_sqlite").ConfigureAwait(false);
            return 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            await stderr.WriteLineAsync("Refusing to run: the identity SQLite database has an active writer/lock.").ConfigureAwait(false);
            return 3;
        }
        catch (Exception ex)
        {
            return await WriteFailureAsync(stderr, ex.Message).ConfigureAwait(false);
        }
    }

    private static SqliteTradingUserDirectory CreateDirectory(string database) =>
        new(
            Options.Create(new IdentityDirectoryOptions
            {
                Provider = IdentityDirectoryProviders.Sqlite,
                Path = database,
                MigrateOnStartup = true,
                ImportLegacyUsersOnStartup = false,
                ExpectedWriterCount = 1,
                BusyTimeoutMilliseconds = 5_000,
            }),
            NullLogger<SqliteTradingUserDirectory>.Instance);

    private static async Task RefuseActiveWriterAsync(string database)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 1;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            await begin.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await using (var rollback = connection.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                continue;
            result[args[i][2..]] = args[++i];
        }
        return result;
    }

    private static bool TryRequire(
        IReadOnlyDictionary<string, string> parsed,
        TextWriter stderr,
        params string[] options)
    {
        var missing = options.Where(option =>
            !parsed.TryGetValue(option, out var value) || string.IsNullOrWhiteSpace(value)).ToArray();
        if (missing.Length == 0)
            return true;

        stderr.WriteLine($"Missing required option(s): {string.Join(", ", missing.Select(option => $"--{option}"))}.");
        return false;
    }

    private static int UnknownCommand(TextWriter stderr)
    {
        stderr.WriteLine("Unknown command.");
        PrintUsage(stderr);
        return 2;
    }

    private static async Task<int> WriteFailureAsync(TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync(message).ConfigureAwait(false);
        return 1;
    }

    private static Task WriteJsonAsync(TextWriter stdout, object payload) =>
        stdout.WriteLineAsync(JsonSerializer.Serialize(payload));

    private static void PrintUsage(TextWriter stderr)
    {
        stderr.WriteLine("""
            Usage:
              B3.Trading.IdentityMaintenance backup \
                --database /var/lib/b3trading/identity/users.db \
                --destination /backup/users.db

              B3.Trading.IdentityMaintenance validate \
                --database /restore/users.db

              B3.Trading.IdentityMaintenance recover-admin \
                --database /var/lib/b3trading/identity/users.db \
                --trading-user-id admin \
                --display-name "Break-glass admin" \
                --firm-id FIRM01 \
                --operator ops-user \
                --change-ticket INC-1234

            backup is safe while trading-host is live and emits JSON metadata.
            validate opens an existing database read-only and never creates or migrates it.
            recover-admin requires trading-host to be scaled down and refuses an active writer.
            """);
    }
}
