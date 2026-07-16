using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Identity;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Pass-1 review (#322) P1.2. Regression coverage for the
/// admin-mutation fail-open audit gap: prior to the fix the
/// AuditLogger swallowed <see cref="WalBackpressureException"/>
/// even for security-sensitive admin endpoints, so a kill-switch
/// toggle could commit without an audit record being durably
/// captured. The fix introduces a fail-closed <c>LogOrFail</c>
/// mode + audit-first ordering: when the audit append throws
/// backpressure, the endpoint returns HTTP 503 and the business
/// dispatch never runs.
/// </summary>
public class AdminMutationAuditFailClosedTests
{
    private sealed class BackpressuringAuditLogger : IAuditLogger
    {
        public int LogCalls;
        public int LogOrFailCalls;
        public void Log(AuditLogEvent evt) => Interlocked.Increment(ref LogCalls);
        public void LogOrFail(AuditLogEvent evt)
        {
            Interlocked.Increment(ref LogOrFailCalls);
            // Simulate the WAL writer rejecting the audit envelope —
            // the contract is that LogOrFail propagates this so the
            // caller can refuse the business mutation.
            throw new WalBackpressureException("test-injected backpressure");
        }
    }

    private sealed class SuccessfulExternalIdentityValidator : IExternalIdentityTokenValidator
    {
        public Task<ExternalIdentityValidationResult> ValidateAsync(string bearerToken, CancellationToken ct = default) =>
            Task.FromResult(new ExternalIdentityValidationResult(
                ExternalIdentityValidationStatus.Success,
                "ok",
                Issuer: "https://issuer.example/v2.0",
                Subject: "external-subject",
                TenantId: "tenant",
                ObjectId: "object"));
    }

    private static TestAppFactory NewFactory(out BackpressuringAuditLogger fake)
    {
        var f = new BackpressuringAuditLogger();
        fake = f;
        return TestAppFactory.WithOverrides(
            configOverrides: new Dictionary<string, string?>(),
            services: s =>
            {
                s.RemoveAll(typeof(IAuditLogger));
                s.AddSingleton<IAuditLogger>(f);
            });
    }

    [Fact]
    public async Task AdminKill_AuditBackpressured_Returns503_AndKillSwitchNotToggled()
    {
        var factory = NewFactory(out var fake);
        using var _ = factory;
        using var admin = await factory.CreateAuthedClientAsync("admin");
        // Pre-condition: end-client not killed.
        var svc = factory.Services.GetRequiredService<KillSwitchService>();
        var ec = new EndClientId("victim-1");
        Assert.False(svc.IsEndClientKilled(ec));

        var resp = await admin.PostAsync("/admin/kill/end-client/victim-1", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.True(fake.LogOrFailCalls >= 1, "expected LogOrFail invoked (audit-first ordering)");
        // Business mutation MUST NOT have committed.
        Assert.False(svc.IsEndClientKilled(ec));
        // KillSwitchService also exposes ListKilledEndClients; the
        // attempted target must be absent there too.
        Assert.DoesNotContain("victim-1", svc.ListKilledEndClients());
    }

    [Fact]
    public async Task SubAccountCreate_AuditBackpressured_Returns503_AndRegistryNotMutated()
    {
        var factory = NewFactory(out var fake);
        using var _ = factory;
        using var admin = await factory.CreateAuthedClientAsync("admin");
        var registry = factory.Services.GetRequiredService<SubAccountsRegistry>();
        var firm = "default"; // anon firm assignment in TestAppFactory
        var before = registry.ListForFirm(firm).Count;

        var resp = await admin.PostAsJsonAsync(
            "/sub-accounts/",
            new SubAccountCreateRequest("subA", "Display"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.True(fake.LogOrFailCalls >= 1);
        // Registry must not contain the would-be sub-account.
        Assert.Equal(before, registry.ListForFirm(firm).Count);
    }

    public static IEnumerable<object[]> IdentityDirectoryProviders()
    {
        yield return new object[] { "InMemory", new Dictionary<string, string?>() };
        var sqlitePath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "TestResults",
            "IdentityAuditFailClosed",
            Guid.NewGuid().ToString("N"),
            "users.db");
        yield return new object[]
        {
            "Sqlite",
            new Dictionary<string, string?>
            {
                ["Trading:IdentityDirectory:Provider"] = "Sqlite",
                ["Trading:IdentityDirectory:Path"] = sqlitePath,
            },
        };
    }

    [Theory]
    [MemberData(nameof(IdentityDirectoryProviders))]
    public async Task IdentityBind_AuditBackpressured_Returns503_AndDirectoryNotMutated(
        string provider,
        Dictionary<string, string?> config)
    {
        using var factory = NewIdentityFactory(config, out var fake);
        using var admin = await factory.CreateAuthedClientAsync("admin");
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var before = await directory.GetUserAsync("alice");

        var resp = await admin.PostAsJsonAsync(
            "/admin/identity/users/alice/external-bindings",
            new { externalAccessToken = "bounded-token", expectedRowVersion = before!.RowVersion });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.True(fake.LogOrFailCalls >= 1);
        var after = await directory.GetUserAsync("alice");
        AssertDirectoryUserUnchanged(provider, before, after);
        Assert.Empty(after!.ExternalIdentities);
    }

    [Theory]
    [MemberData(nameof(IdentityDirectoryProviders))]
    public async Task IdentityUnbind_AuditBackpressured_Returns503_AndDirectoryNotMutated(
        string provider,
        Dictionary<string, string?> config)
    {
        using var factory = NewIdentityFactory(config, out var fake);
        using var admin = await factory.CreateAuthedClientAsync("admin");
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var alice = await directory.GetUserAsync("alice");
        var binding = await directory.BindExternalIdentityAsync(
            "alice",
            new ExternalIdentityBindingRequest("https://issuer.example/v2.0", "existing-subject"),
            alice!.RowVersion);
        var before = await directory.GetUserAsync("alice");

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/admin/identity/users/alice/external-bindings/{binding.Id}")
        {
            Content = JsonContent.Create(new { expectedRowVersion = before!.RowVersion }),
        };
        var resp = await admin.SendAsync(req);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.True(fake.LogOrFailCalls >= 1);
        var after = await directory.GetUserAsync("alice");
        AssertDirectoryUserUnchanged(provider, before, after);
        Assert.Single(after!.ExternalIdentities);
    }

    [Theory]
    [MemberData(nameof(IdentityDirectoryProviders))]
    public async Task IdentityStatus_AuditBackpressured_Returns503_AndDirectoryNotMutated(
        string provider,
        Dictionary<string, string?> config)
    {
        using var factory = NewIdentityFactory(config, out var fake);
        using var admin = await factory.CreateAuthedClientAsync("admin");
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var before = await directory.GetUserAsync("alice");

        var resp = await admin.PutAsJsonAsync(
            "/admin/identity/users/alice/status",
            new { status = TradingUserDirectoryConstants.StatusDisabled, expectedRowVersion = before!.RowVersion });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.True(fake.LogOrFailCalls >= 1);
        var after = await directory.GetUserAsync("alice");
        AssertDirectoryUserUnchanged(provider, before, after);
        Assert.Equal(TradingUserDirectoryConstants.StatusActive, after!.Status);
    }

    [Theory]
    [MemberData(nameof(IdentityDirectoryProviders))]
    public async Task IdentityAuthorization_AuditBackpressured_Returns503_AndDirectoryNotMutated(
        string provider,
        Dictionary<string, string?> config)
    {
        using var factory = NewIdentityFactory(config, out var fake);
        using var admin = await factory.CreateAuthedClientAsync("admin");
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var before = await directory.GetUserAsync("alice");

        var resp = await admin.PutAsJsonAsync(
            "/admin/identity/users/alice/authorization",
            new { firmId = "FIRM77", role = TradingUserDirectoryConstants.RoleCompliance, expectedRowVersion = before!.RowVersion });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.True(fake.LogOrFailCalls >= 1);
        var after = await directory.GetUserAsync("alice");
        AssertDirectoryUserUnchanged(provider, before, after);
        Assert.Equal(before.FirmId, after!.FirmId);
        Assert.Equal(before.Role, after.Role);
    }

    private static TestAppFactory NewIdentityFactory(
        Dictionary<string, string?> config,
        out BackpressuringAuditLogger fake)
    {
        var f = new BackpressuringAuditLogger();
        fake = f;
        return TestAppFactory.WithOverrides(config, services =>
        {
            services.RemoveAll(typeof(IAuditLogger));
            services.AddSingleton<IAuditLogger>(f);
            services.RemoveAll<IExternalIdentityTokenValidator>();
            services.AddSingleton<IExternalIdentityTokenValidator, SuccessfulExternalIdentityValidator>();
        });
    }

    private static void AssertDirectoryUserUnchanged(string provider, TradingUser? before, TradingUser? after)
    {
        _ = provider;
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.TradingUserId, after!.TradingUserId);
        Assert.Equal(before.DisplayName, after.DisplayName);
        Assert.Equal(before.FirmId, after.FirmId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Role, after.Role);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(before.ExternalIdentities.Count, after.ExternalIdentities.Count);
    }
}
