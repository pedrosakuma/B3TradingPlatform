using B3.Trading.Application.Identity;
using B3.Trading.Infrastructure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Identity;

public sealed class TradingUserDirectoryContractTests
{
    public static IEnumerable<object[]> Providers()
    {
        yield return new object[] { "InMemory", new Func<string, ITradingUserDirectory>(_ => new InMemoryTradingUserDirectory()) };
        yield return new object[] { "Sqlite", new Func<string, ITradingUserDirectory>(dir => NewSqlite(System.IO.Path.Combine(dir, "users.db"))) };
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportLegacyUsers_IsIdempotent_AndPreservesExactAuthorization(
        string name,
        Func<string, ITradingUserDirectory> factory)
    {
        using var workspace = TestWorkspace.Create(name);
        var directory = factory(workspace.Path);
        await directory.InitializeAsync();

        var imported = await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("Alice.Raw", "Alice.Raw", "FIRM01", TradingUserDirectoryConstants.RoleAdmin),
            new LegacyTradingUserImport("bob", "bob", "FIRM02", TradingUserDirectoryConstants.RoleUser),
        });
        var second = await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("Alice.Raw", "Alice.Raw", "FIRM01", TradingUserDirectoryConstants.RoleAdmin),
            new LegacyTradingUserImport("bob", "bob", "FIRM02", TradingUserDirectoryConstants.RoleUser),
        });

        Assert.Equal(2, imported);
        Assert.Equal(0, second);
        var alice = await directory.GetUserAsync("Alice.Raw");
        Assert.NotNull(alice);
        Assert.Equal("Alice.Raw", alice.TradingUserId);
        Assert.Equal("FIRM01", alice.FirmId);
        Assert.Equal(TradingUserDirectoryConstants.RoleAdmin, alice.Role);
        Assert.Equal(TradingUserDirectoryConstants.StatusActive, alice.Status);
        Assert.Equal(1, alice.RowVersion);
        Assert.Null(await directory.GetUserAsync("alice.raw"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportLegacyUsers_RejectsCaseInsensitiveCollisionsAndInvalidAuthorization(
        string name,
        Func<string, ITradingUserDirectory> factory)
    {
        using var workspace = TestWorkspace.Create(name);
        var directory = factory(workspace.Path);
        await directory.InitializeAsync();

        await Assert.ThrowsAsync<TradingUserDirectoryValidationException>(() =>
            directory.ImportLegacyUsersAsync(new[]
            {
                new LegacyTradingUserImport("Alice", "Alice", "FIRM01", TradingUserDirectoryConstants.RoleUser),
                new LegacyTradingUserImport("alice", "alice", "FIRM01", TradingUserDirectoryConstants.RoleUser),
            }));
        await Assert.ThrowsAsync<TradingUserDirectoryValidationException>(() =>
            directory.ImportLegacyUsersAsync(new[]
            {
                new LegacyTradingUserImport("\u212A", "\u212A", "FIRM01", TradingUserDirectoryConstants.RoleUser),
                new LegacyTradingUserImport("k", "k", "FIRM01", TradingUserDirectoryConstants.RoleUser),
            }));
        await Assert.ThrowsAsync<TradingUserDirectoryValidationException>(() =>
            directory.ImportLegacyUsersAsync(new[]
            {
                new LegacyTradingUserImport("charlie", "charlie", "", TradingUserDirectoryConstants.RoleUser),
            }));
        await Assert.ThrowsAsync<TradingUserDirectoryValidationException>(() =>
            directory.ImportLegacyUsersAsync(new[]
            {
                new LegacyTradingUserImport("charlie", "charlie", "FIRM01", "owner"),
            }));

        await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("Existing", "Existing", "FIRM01", TradingUserDirectoryConstants.RoleUser),
        });
        await Assert.ThrowsAsync<TradingUserDirectoryValidationException>(() =>
            directory.ImportLegacyUsersAsync(new[]
            {
                new LegacyTradingUserImport("existing", "existing", "FIRM01", TradingUserDirectoryConstants.RoleUser),
            }));

        await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("\u212A-existing", "\u212A-existing", "FIRM01", TradingUserDirectoryConstants.RoleUser),
        });
        await Assert.ThrowsAsync<TradingUserDirectoryValidationException>(() =>
            directory.ImportLegacyUsersAsync(new[]
            {
                new LegacyTradingUserImport("k-existing", "k-existing", "FIRM01", TradingUserDirectoryConstants.RoleUser),
            }));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExternalIdentityBindings_AreBinaryUniqueAndBumpRowVersion(
        string name,
        Func<string, ITradingUserDirectory> factory)
    {
        using var workspace = TestWorkspace.Create(name);
        var directory = factory(workspace.Path);
        await directory.InitializeAsync();
        await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("Alice", "Alice", "FIRM01", TradingUserDirectoryConstants.RoleUser),
            new LegacyTradingUserImport("bob", "bob", "FIRM01", TradingUserDirectoryConstants.RoleUser),
        });

        var binding = await directory.BindExternalIdentityAsync(
            "Alice",
            new ExternalIdentityBindingRequest("https://issuer.example/v2.0", "SubjectA", "tenant", "object"),
            expectedRowVersion: 1);
        var aliceAfterBind = await directory.GetUserAsync("Alice");

        Assert.Equal(1, binding.Id);
        Assert.NotNull(aliceAfterBind);
        Assert.Equal(2, aliceAfterBind.RowVersion);
        Assert.NotNull(await directory.ResolveExternalIdentityAsync("https://issuer.example/v2.0", "SubjectA"));
        Assert.Null(await directory.ResolveExternalIdentityAsync("https://issuer.example/v2.0", "subjecta"));
        await Assert.ThrowsAsync<TradingUserDirectoryConcurrencyException>(() =>
            directory.BindExternalIdentityAsync(
                "Alice",
                new ExternalIdentityBindingRequest("https://issuer.example/v2.0", "Other"),
                expectedRowVersion: 1));
        await Assert.ThrowsAsync<TradingUserDirectoryConflictException>(() =>
            directory.BindExternalIdentityAsync(
                "bob",
                new ExternalIdentityBindingRequest("https://issuer.example/v2.0", "SubjectA"),
                expectedRowVersion: 1));

        await directory.UnbindExternalIdentityAsync("Alice", binding.Id, expectedRowVersion: 2);
        var aliceAfterUnbind = await directory.GetUserAsync("Alice");
        Assert.NotNull(aliceAfterUnbind);
        Assert.Equal(3, aliceAfterUnbind.RowVersion);
        Assert.Empty(aliceAfterUnbind.ExternalIdentities);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StatusFirmAndRole_AreConstrainedAndOptimistic(
        string name,
        Func<string, ITradingUserDirectory> factory)
    {
        using var workspace = TestWorkspace.Create(name);
        var directory = factory(workspace.Path);
        await directory.InitializeAsync();
        await directory.ImportLegacyUsersAsync(new[]
        {
            new LegacyTradingUserImport("alice", "alice", "FIRM01", TradingUserDirectoryConstants.RoleUser),
        });

        await directory.SetStatusAsync("alice", TradingUserDirectoryConstants.StatusDisabled, expectedRowVersion: 1);
        await Assert.ThrowsAsync<TradingUserDirectoryConcurrencyException>(() =>
            directory.SetFirmAndRoleAsync("alice", "FIRM02", TradingUserDirectoryConstants.RoleCompliance, expectedRowVersion: 1));
        await directory.SetFirmAndRoleAsync("alice", "FIRM02", TradingUserDirectoryConstants.RoleCompliance, expectedRowVersion: 2);
        var alice = await directory.GetUserAsync("alice");

        Assert.NotNull(alice);
        Assert.Equal(TradingUserDirectoryConstants.StatusDisabled, alice.Status);
        Assert.Equal("FIRM02", alice.FirmId);
        Assert.Equal(TradingUserDirectoryConstants.RoleCompliance, alice.Role);
        Assert.Equal(3, alice.RowVersion);
        await Assert.ThrowsAsync<TradingUserDirectoryValidationException>(() =>
            directory.SetStatusAsync("alice", "locked", expectedRowVersion: 3));
    }

    private static SqliteTradingUserDirectory NewSqlite(string path) =>
        new(
            Options.Create(new IdentityDirectoryOptions
            {
                Provider = IdentityDirectoryProviders.Sqlite,
                Path = path,
                BusyTimeoutMilliseconds = 5000,
            }),
            NullLogger<SqliteTradingUserDirectory>.Instance);
}
