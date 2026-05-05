using B3.Trading.Api.Auth;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Tests;

public class InMemoryUserStoreTests
{
    private static InMemoryUserStore Build(params UserConfig[] seeded)
    {
        var opts = Options.Create(new AuthOptions { Users = seeded.ToList() });
        return new InMemoryUserStore(opts);
    }

    private static UserConfig User(string username, string role = "user", string firm = "FIRM01") =>
        new()
        {
            Username = username,
            PasswordHash = "h",
            Salt = "s",
            Iterations = 1,
            Role = role,
            Firm = firm,
        };

    [Fact]
    public void Hydrates_seeded_users_from_options()
    {
        var store = Build(User("alice"), User("bob"));
        Assert.True(store.TryGet("alice", out var a));
        Assert.Equal("alice", a!.Username);
        Assert.True(store.TryGet("BOB", out var b));
        Assert.Equal("bob", b!.Username);
    }

    [Fact]
    public void TryAdd_runtime_user_succeeds_and_is_visible()
    {
        var store = Build();
        Assert.True(store.TryAdd(User("carol")));
        Assert.True(store.TryGet("carol", out var c));
        Assert.Equal("carol", c!.Username);
    }

    [Fact]
    public void TryAdd_runtime_collision_returns_false()
    {
        var store = Build();
        Assert.True(store.TryAdd(User("dave")));
        Assert.False(store.TryAdd(User("DAVE")));
    }

    [Fact]
    public void TryAdd_envseeded_collision_returns_false_and_does_not_shadow()
    {
        var store = Build(User("alice", role: "user", firm: "ORIG"));
        Assert.False(store.TryAdd(User("ALICE", role: "admin", firm: "FAKE")));
        Assert.True(store.TryGet("alice", out var u));
        Assert.Equal("ORIG", u!.Firm);
        Assert.Equal("user", u.Role);
    }

    [Fact]
    public void TryGet_missing_returns_false()
    {
        var store = Build(User("alice"));
        Assert.False(store.TryGet("nobody", out var u));
        Assert.Null(u);
    }

    [Fact]
    public void TryGet_with_blank_returns_false()
    {
        var store = Build(User("alice"));
        Assert.False(store.TryGet("", out _));
        Assert.False(store.TryGet("  ", out _));
    }
}
