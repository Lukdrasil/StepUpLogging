using System.Diagnostics.Metrics;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>Sums the measurements of the <c>StepUpLogging.Audit</c> meter's counters, by instrument.</summary>
internal sealed class AuditMeterTotals : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _totals = [];

    public AuditMeterTotals()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "StepUpLogging.Audit")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            lock (_totals)
            {
                _totals[instrument.Name] = _totals.TryGetValue(instrument.Name, out var total) ? total + measurement : measurement;
            }
        });
        _listener.Start();
    }

    public long Total(string instrumentName)
    {
        lock (_totals)
        {
            return _totals.TryGetValue(instrumentName, out var total) ? total : 0;
        }
    }

    /// <summary>Polls every enabled observable instrument (e.g. a gauge) once, recording its current value.</summary>
    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();
}
