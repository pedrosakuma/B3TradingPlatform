using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application.Identity;
using B3.Trading.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests.Identity;

public sealed class IdentityDirectoryStartupTests
{
    [Fact]
    public async Task SqliteProvider_MigratesImportsLegacyUsers_AndSurfacesHealth()
    {
        using var workspace = TestWorkspace.Create(nameof(SqliteProvider_MigratesImportsLegacyUsers_AndSurfacesHealth));
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:IdentityDirectory:Provider"] = "Sqlite",
            ["Trading:IdentityDirectory:Path"] = Path.Combine(workspace.Path, "identity", "users.db"),
            ["Trading:Persistence:DataDirectory"] = workspace.Path,
        });
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/ready");
        var health = await client.GetFromJsonAsync<JsonElement>("/health");
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var alice = await directory.GetUserAsync(TestAppFactory.TestUser);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(health.GetProperty("identityDirectory").GetProperty("ready").GetBoolean());
        Assert.Equal("Sqlite", health.GetProperty("identityDirectory").GetProperty("provider").GetString());
        Assert.Equal(SqliteTradingUserDirectory.CurrentSchemaVersion, health.GetProperty("identityDirectory").GetProperty("schemaVersion").GetInt32());
        Assert.NotNull(alice);
        Assert.Equal(TestAppFactory.TestUser, alice.TradingUserId);
    }

    [Fact]
    public async Task SqliteProvider_UnusableDirectory_FailsStartupClosed()
    {
        using var workspace = TestWorkspace.Create(nameof(SqliteProvider_UnusableDirectory_FailsStartupClosed));
        var db = Path.Combine(workspace.Path, "users.db");
        await File.WriteAllTextAsync(db, "not sqlite");
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:IdentityDirectory:Provider"] = "Sqlite",
            ["Trading:IdentityDirectory:Path"] = db,
            ["Trading:Persistence:DataDirectory"] = workspace.Path,
        });

        Assert.Throws<TradingUserDirectoryUnavailableException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task DefaultPath_IsDerivedFromPersistenceDataDirectory()
    {
        using var workspace = TestWorkspace.Create(nameof(DefaultPath_IsDerivedFromPersistenceDataDirectory));
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:IdentityDirectory:Provider"] = "Sqlite",
            ["Trading:Persistence:DataDirectory"] = workspace.Path,
        });
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/ready");
        var expected = Path.Combine(workspace.Path, "identity", "users.db");
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(expected, directory.StorePath);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task ReadyFailsWhenSqliteDirectoryBecomesUnavailable_ButLiveStaysProcessOnly()
    {
        using var workspace = TestWorkspace.Create(nameof(ReadyFailsWhenSqliteDirectoryBecomesUnavailable_ButLiveStaysProcessOnly));
        var db = Path.Combine(workspace.Path, "identity", "users.db");
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:IdentityDirectory:Provider"] = "Sqlite",
            ["Trading:IdentityDirectory:Path"] = db,
            ["Trading:Persistence:DataDirectory"] = workspace.Path,
        });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);

        File.Delete(db);
        File.Delete(db + "-wal");
        File.Delete(db + "-shm");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/live")).StatusCode);
    }

    internal sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string path) => Path = path;
        public string Path { get; }

        public static TestWorkspace Create(string name)
        {
            var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            var root = System.IO.Path.Combine(
                Directory.GetCurrentDirectory(),
                "TestResults",
                "Identity",
                safe + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestWorkspace(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
