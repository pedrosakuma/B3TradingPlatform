using System.Net;
using System.Net.Http.Json;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
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
}
