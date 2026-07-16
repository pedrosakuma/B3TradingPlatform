using B3.Trading.Application.Identity;
using B3.Trading.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

return await IdentityMaintenanceCli.RunAsync(args);

internal static class IdentityMaintenanceCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "recover-admin", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Unknown command.");
            PrintUsage();
            return 2;
        }

        var parsed = Parse(args.Skip(1).ToArray());
        if (!parsed.TryGetValue("database", out var database)
            || !parsed.TryGetValue("trading-user-id", out var tradingUserId)
            || !parsed.TryGetValue("display-name", out var displayName)
            || !parsed.TryGetValue("firm-id", out var firmId)
            || !parsed.TryGetValue("operator", out var operatorId)
            || !parsed.TryGetValue("change-ticket", out var changeTicket))
        {
            Console.Error.WriteLine("Missing required option.");
            PrintUsage();
            return 2;
        }

        try
        {
            if (File.Exists(database))
                await RefuseActiveWriterAsync(database);

            var directory = new SqliteTradingUserDirectory(
                Options.Create(new IdentityDirectoryOptions
                {
                    Provider = IdentityDirectoryProviders.Sqlite,
                    Path = database,
                    MigrateOnStartup = true,
                    ImportLegacyUsersOnStartup = false,
                    ExpectedWriterCount = 1,
                    BusyTimeoutMilliseconds = 1000,
                }),
                NullLogger<SqliteTradingUserDirectory>.Instance);

            await directory.InitializeAsync();
            await RefuseActiveWriterAsync(database);
            var result = await directory.EnsureRecoveryAdminAsync(new RecoveryAdminRequest(
                tradingUserId,
                displayName,
                firmId,
                operatorId,
                changeTicket));

            Console.WriteLine(
                $"recovery_admin={(result.Created ? "created" : "enabled")} trading_user_id={result.User.TradingUserId} row_version={result.User.RowVersion} maintenance_event_id={result.MaintenanceEventId}");
            Console.WriteLine("password_material=legacy_secret_config_not_sqlite");
            return 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            Console.Error.WriteLine("Refusing to run: the identity SQLite database has an active writer/lock.");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

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
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 1;";
            await pragma.ExecuteNonQueryAsync();
        }
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            await begin.ExecuteNonQueryAsync();
        }
        await using (var rollback = connection.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
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

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage:
              B3.Trading.IdentityMaintenance recover-admin \
                --database /var/lib/b3trading/identity/users.db \
                --trading-user-id admin \
                --display-name "Break-glass admin" \
                --firm-id FIRM01 \
                --operator ops-user \
                --change-ticket INC-1234

            Creates or enables exactly one local recovery admin authorization row.
            Password material is still supplied through legacy Trading:Auth:Users secret config, never SQLite.
            Run only with the trading-host scaled down; the tool refuses a locked SQLite writer.
            """);
    }
}
