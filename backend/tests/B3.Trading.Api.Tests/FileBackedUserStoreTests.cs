using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Api.Auth;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Slice 3 of #97 — runtime users persist to <c>users.json</c> across
/// host restarts. Tests stand up TWO factories sharing the same temp
/// file path and verify the second can authenticate users created
/// against the first. Env-seeded users are NEVER serialized to disk —
/// configuration is the only source of truth for them.
/// </summary>
public class FileBackedUserStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public FileBackedUserStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "b3-userstore-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "users.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private TestAppFactory MakeFactory()
        => TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:UserStore:Enabled"] = "true",
            ["Trading:Auth:UserStore:FilePath"] = _filePath,
        });

    private static string FreshUsername() => "u" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Signup_PersistsToFile_AndLoginSurvivesRestart()
    {
        var username = FreshUsername();

        // First boot: signup the runtime user.
        using (var factory = MakeFactory())
        using (var client = factory.CreateClient())
        {
            var resp = await client.PostAsJsonAsync("/api/auth/signup",
                new SignupRequest(username, "wonderland-1"));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // File must exist with the user serialized.
        Assert.True(File.Exists(_filePath));
        var json = JsonDocument.Parse(File.ReadAllText(_filePath)).RootElement;
        Assert.Equal(1, json.GetProperty("Version").GetInt32());
        var users = json.GetProperty("Users").EnumerateArray().ToList();
        Assert.Single(users);
        Assert.Equal(username, users[0].GetProperty("Username").GetString());

        // Second boot reads the same file. Login must succeed without
        // re-signup — proves the persisted hash/salt round-tripped.
        using (var factory = MakeFactory())
        using (var client = factory.CreateClient())
        {
            var login = await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest(username, "wonderland-1"));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task EnvSeededUser_IsNeverWrittenToFile()
    {
        // alice/bob/admin are env-seeded by TestAppFactory. Run a single
        // signup to force a flush, then assert the file contains ONLY
        // the runtime user — env-seeded must not leak to disk.
        var runtimeUser = FreshUsername();
        using (var factory = MakeFactory())
        using (var client = factory.CreateClient())
        {
            var resp = await client.PostAsJsonAsync("/api/auth/signup",
                new SignupRequest(runtimeUser, "wonderland-1"));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        var json = JsonDocument.Parse(File.ReadAllText(_filePath)).RootElement;
        var usernames = json.GetProperty("Users").EnumerateArray()
            .Select(u => u.GetProperty("Username").GetString())
            .ToList();
        Assert.Equal(new[] { runtimeUser }, usernames);
    }

    [Fact]
    public async Task CorruptFile_DoesNotBrickBoot_StartsWithEmptyRuntimeSet()
    {
        // Pre-poison the file with garbage. The store should log a warning
        // and treat the runtime set as empty — env-seeded users still work.
        await File.WriteAllTextAsync(_filePath, "not valid json {[}");

        using var factory = MakeFactory();
        using var client = factory.CreateClient();

        // Env-seeded login still works.
        var aliceLogin = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("alice", "wonderland"));
        Assert.Equal(HttpStatusCode.OK, aliceLogin.StatusCode);

        // Signup still works (and the corrupt file gets overwritten).
        var fresh = FreshUsername();
        var signup = await client.PostAsJsonAsync("/api/auth/signup",
            new SignupRequest(fresh, "wonderland-1"));
        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);

        // File is now valid JSON with exactly one user.
        var json = JsonDocument.Parse(File.ReadAllText(_filePath)).RootElement;
        Assert.Single(json.GetProperty("Users").EnumerateArray());
    }

    [Fact]
    public async Task RuntimeUser_CannotShadowEnvSeeded_AfterFileTampering()
    {
        // Hand-craft a file that pretends to override "alice" with a
        // different password hash. Env-seeded must win on read AND we
        // must reject any future signup attempt for "alice" with 409.
        var poisoned = """
            {
              "Version": 1,
              "Users": [
                { "Username": "alice", "PasswordHash": "ZmFrZQ==", "Salt": "ZmFrZQ==",
                  "Iterations": 1, "Role": "user", "Firm": "FIRM01" }
              ]
            }
            """;
        await File.WriteAllTextAsync(_filePath, poisoned);

        using var factory = MakeFactory();
        using var client = factory.CreateClient();

        // Real alice still authenticates with the env-seeded password.
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("alice", "wonderland"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Attempt to signup as alice → 409 (env collision check).
        var signup = await client.PostAsJsonAsync("/api/auth/signup",
            new SignupRequest("alice", "wonderland-1"));
        Assert.Equal(HttpStatusCode.Conflict, signup.StatusCode);
    }

    [Fact]
    public async Task DuplicateRuntimeSignup_Returns409_AndDoesNotDoubleWrite()
    {
        var username = FreshUsername();
        using var factory = MakeFactory();
        using var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/auth/signup",
            new SignupRequest(username, "wonderland-1"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/auth/signup",
            new SignupRequest(username, "wonderland-2"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var json = JsonDocument.Parse(File.ReadAllText(_filePath)).RootElement;
        var matching = json.GetProperty("Users").EnumerateArray()
            .Where(u => u.GetProperty("Username").GetString() == username)
            .ToList();
        Assert.Single(matching);
    }
}
