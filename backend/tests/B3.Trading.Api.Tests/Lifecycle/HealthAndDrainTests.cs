using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B3.Trading.Api.Tests.Lifecycle;

public class HealthAndDrainTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public HealthAndDrainTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Live_Always_Returns_Ok()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/live");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns_Ok_When_Not_Draining()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Health_Returns_Json_With_Persistence_Block()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("uptime", out _));
        var p = root.GetProperty("persistence");
        Assert.False(p.GetProperty("enabled").GetBoolean()); // tests disable persistence
        Assert.True(p.GetProperty("healthy").GetBoolean());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("terminalFault").ValueKind);
        Assert.NotEqual(Guid.Empty, p.GetProperty("walGeneration").GetGuid());
        Assert.Equal(0, p.GetProperty("lastAdmittedSeq").GetInt64());
        Assert.Equal(0, p.GetProperty("lastAppendedSeq").GetInt64());
        Assert.Equal(0, p.GetProperty("lastLogFsyncedSeq").GetInt64());
        Assert.Equal(0, p.GetProperty("lastCommittedSeq").GetInt64());
    }

    [Fact]
    public async Task Drain_Causes_Ready_To_Return_503_And_Health_Status_Draining()
    {
        // We need a per-test factory so flipping the drain flag does not
        // poison other tests sharing the IClassFixture instance.
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var drain = factory.Services.GetRequiredService<DrainState>();
        drain.BeginDrain();

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal("draining", doc.RootElement.GetProperty("status").GetString());

        // Live must keep returning 200 even while draining.
        var live = await client.GetAsync("/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Drain_Causes_Order_Submit_To_Return_503()
    {
        using var factory = new TestAppFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var drain = factory.Services.GetRequiredService<DrainState>();
        drain.BeginDrain();

        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30.0m,
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public void OnlyOutboundReconciliationDrain_CanResume()
    {
        var drain = new DrainState();
        drain.BeginDrain("outbound_cancel_replace_reconciliation_required");

        Assert.True(drain.TryEndOutboundReconciliationDrain());
        Assert.False(drain.IsDraining);

        drain.BeginDrain("host_stopping");
        Assert.False(drain.TryEndOutboundReconciliationDrain());
        Assert.True(drain.IsDraining);

        var overlapping = new DrainState();
        overlapping.BeginDrain("outbound_new_order_reconciliation_required");
        overlapping.BeginDrain("wal_execution_report_rejected");
        Assert.False(overlapping.TryEndOutboundReconciliationDrain());
        Assert.True(overlapping.IsDraining);
        Assert.Equal("wal_execution_report_rejected", overlapping.Reason);
    }

    [Fact]
    public void OnlyColdStartLifecycleIntentsDrain_CanResume()
    {
        var drain = new DrainState();
        drain.BeginDrain("cold_start_unresolved_lifecycle_intents");

        Assert.True(drain.TryEndColdStartLifecycleIntentsDrain());
        Assert.False(drain.IsDraining);

        drain.BeginDrain("host_stopping");
        Assert.False(drain.TryEndColdStartLifecycleIntentsDrain());
        Assert.True(drain.IsDraining);

        var overlapping = new DrainState();
        overlapping.BeginDrain("cold_start_unresolved_lifecycle_intents");
        overlapping.BeginDrain("wal_execution_report_rejected");
        Assert.False(overlapping.TryEndColdStartLifecycleIntentsDrain());
        Assert.True(overlapping.IsDraining);
        Assert.Equal("wal_execution_report_rejected", overlapping.Reason);
    }

    [Fact]
    public void BlocksPodRouting_ExcludesOutboundReconciliationReasons_ButNotOthers()
    {
        // #780: an outbound-reconciliation drain (new-order, cancel/replace,
        // or the cold-start unresolved-lifecycle-intents latch) is a
        // per-firm/per-end-client order-submission concern, not a reason to
        // pull the whole pod out of Service rotation.
        foreach (var reconciliationReason in new[]
                 {
                     "outbound_new_order_reconciliation_required",
                     "outbound_cancel_replace_reconciliation_required",
                     "cold_start_unresolved_lifecycle_intents",
                 })
        {
            var drain = new DrainState();
            drain.BeginDrain(reconciliationReason);
            Assert.True(drain.IsDraining);
            Assert.False(drain.BlocksPodRouting);
        }

        // A genuinely pod-unfit reason (shutdown, unclassified fault) must
        // still block routing.
        var stopping = new DrainState();
        stopping.BeginDrain("host_stopping");
        Assert.True(stopping.BlocksPodRouting);

        // Mixing a reconciliation reason with a hard reason must still
        // block routing (the hard reason dominates).
        var mixed = new DrainState();
        mixed.BeginDrain("cold_start_unresolved_lifecycle_intents");
        mixed.BeginDrain("wal_execution_report_rejected");
        Assert.True(mixed.BlocksPodRouting);
    }

    [Fact]
    public async Task WalFault_Causes_Ready503_While_LiveRemainsOk()
    {
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>(),
            services =>
            {
                services.RemoveAll<IEventStoreHealth>();
                services.AddSingleton<IEventStoreHealth>(
                    new FaultedEventStoreHealth(new IOException("disk full")));
            });
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        var live = await client.GetAsync("/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal("unready", doc.RootElement.GetProperty("status").GetString());
        var persistence = doc.RootElement.GetProperty("persistence");
        Assert.False(persistence.GetProperty("healthy").GetBoolean());
        Assert.Equal(nameof(IOException), persistence.GetProperty("terminalFault").GetString());
        Assert.Equal("disk full", persistence.GetProperty("terminalFaultMessage").GetString());
        Assert.Equal(
            "wal_io_fault",
            persistence.GetProperty("terminalFaultDetails").GetProperty("code").GetString());
    }

    [Fact]
    public async Task LegacyWalMigrationRequired_IsSurfacedWithRecoveryGuidance()
    {
        using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>
            {
                ["Trading:Persistence:Enabled"] = "true",
                ["Trading:Persistence:DataDirectory"] = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "TestResults",
                    "HealthAndDrainTests",
                    Guid.NewGuid().ToString("N")),
                ["Trading:Persistence:FirmId"] = "FIRM01",
            },
            services =>
            {
                services.RemoveAll<IEventStoreHealth>();
                services.AddSingleton<IEventStoreHealth>(
                    new FaultedEventStoreHealth(new WalLegacyMigrationRequiredException(
                        "Non-empty legacy WAL has no commit marker.")));
            });
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        var persistence = doc.RootElement.GetProperty("persistence");
        var details = persistence.GetProperty("terminalFaultDetails");
        Assert.Equal(
            "legacy_wal_migration_required",
            details.GetProperty("code").GetString());
        Assert.Contains(
            "recover-legacy-wal",
            details.GetProperty("recommendedAction").GetString());
        Assert.Contains(
            "--firm-id FIRM01",
            details.GetProperty("recommendedAction").GetString());
    }

    [Fact]
    public async Task Order_Submit_Increments_OrdersSubmitted_Counter()
    {
        using var factory = new TestAppFactory();
        using var client = await factory.CreateAuthedClientAsync();

        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "B3.Trading" && instr.Name == "trading.orders.submitted")
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 10,
            Price = 30.0m,
        });
        resp.EnsureSuccessStatusCode();

        Assert.True(observed >= 1, $"expected at least one increment, observed {observed}");

        // Sanity-check the meter / instrument registration is exactly what
        // OTel/Prometheus would scrape — guards against accidental rename.
        Assert.NotNull(MetricsRegistry.OrdersSubmitted);
        Assert.Equal("B3.Trading", MetricsRegistry.Meter.Name);
    }

    private sealed class FaultedEventStoreHealth(Exception terminalFault) : IEventStoreHealth
    {
        public bool IsHealthy => false;
        public Exception? TerminalFault { get; } = terminalFault;
    }
}
