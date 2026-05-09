using System.Diagnostics.Metrics;
using B3.Trading.Application.Observability;
using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

public class MetricsTests
{
    [Fact]
    public void NegotiateTotal_IncrementsCapturable()
    {
        long captured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "entrypoint_listener.negotiate_total")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            captured += measurement;
        });
        listener.Start();

        FixpListenerMetrics.NegotiateTotal.Add(1, new KeyValuePair<string, object?>("outcome", "ok"));
        listener.RecordObservableInstruments();

        Assert.True(captured >= 1);
    }

    [Fact]
    public void SessionsActive_UpDownCounterWorks()
    {
        int captured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "entrypoint_listener.sessions_active")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
        {
            captured += measurement;
        });
        listener.Start();

        FixpListenerMetrics.SessionsActive.Add(1);
        FixpListenerMetrics.SessionsActive.Add(-1);

        Assert.Equal(0, captured);
    }

    [Fact]
    public void Enabled_IncrementsCapturable()
    {
        int captured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "entrypoint_listener.enabled")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
        {
            captured += measurement;
        });
        listener.Start();

        FixpListenerMetrics.Enabled.Add(1);

        Assert.True(captured >= 1);
    }
}
