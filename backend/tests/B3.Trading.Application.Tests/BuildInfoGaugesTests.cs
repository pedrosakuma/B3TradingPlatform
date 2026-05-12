using System.Diagnostics.Metrics;
using B3.Trading.Application.Observability;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Issue #234 — verifies the build-info gauges report the
/// runtime-configured values of <c>OutboundDrainShutdownTimeout</c>
/// and <c>GroupCommitMaxRecords</c>, and that the source callback is
/// re-read on each observation (so an <c>IOptionsMonitor</c> swap is
/// reflected without re-registration).
/// </summary>
public sealed class BuildInfoGaugesTests
{
    private static (List<Measurement<double>> Timeouts, List<Measurement<int>> GroupCommit) Collect()
    {
        var timeouts = new List<Measurement<double>>();
        var groupCommit = new List<Measurement<int>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name != "B3.Trading") return;
                if (inst.Name == "trading.entrypoint_listener.outbound_drain_shutdown_timeout" ||
                    inst.Name == "trading.persistence.group_commit_max_records")
                {
                    l.EnableMeasurementEvents(inst);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((inst, v, tags, _) =>
        {
            if (inst.Name == "trading.entrypoint_listener.outbound_drain_shutdown_timeout")
                lock (timeouts) timeouts.Add(new Measurement<double>(v, tags.ToArray()));
        });
        listener.SetMeasurementEventCallback<int>((inst, v, tags, _) =>
        {
            if (inst.Name == "trading.persistence.group_commit_max_records")
                lock (groupCommit) groupCommit.Add(new Measurement<int>(v, tags.ToArray()));
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return (timeouts, groupCommit);
    }

    [Fact]
    public void Gauges_Report_Configured_Values_And_Reflect_Live_Source_Changes()
    {
        // Snapshot a configured value through a swappable source — mimics
        // IOptionsMonitor.CurrentValue resolution on each callback.
        var timeoutSeconds = 1.0;
        var maxRecords = 512;
        MetricsRegistry.RegisterOutboundDrainShutdownTimeoutSource(() => timeoutSeconds);
        MetricsRegistry.RegisterGroupCommitMaxRecordsSource(() => maxRecords);

        var (t1, g1) = Collect();
        Assert.Contains(t1, m => m.Value == 1.0);
        Assert.Contains(g1, m => m.Value == 512);
        // Build-info contract: no high-cardinality tags. The gauge is
        // one series per process; verify the emitted measurements carry
        // no tag keys at all.
        Assert.All(t1, m => Assert.Empty(m.Tags.ToArray()));
        Assert.All(g1, m => Assert.Empty(m.Tags.ToArray()));

        // Simulate a hot config reload: closure observes the new value
        // on the next scrape without re-registering the source.
        timeoutSeconds = 2.5;
        maxRecords = 1024;
        var (t2, g2) = Collect();
        Assert.Contains(t2, m => m.Value == 2.5);
        Assert.Contains(g2, m => m.Value == 1024);
    }
}
