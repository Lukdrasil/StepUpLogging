using System.Diagnostics.Metrics;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Sums the measurements of the <c>StepUpLogging.Audit</c> meter's counters, by instrument and by
/// instrument, tag name and tag value.
/// </summary>
internal sealed class AuditMeterTotals : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _totals = [];
    private readonly Dictionary<(string Instrument, string TagName, string TagValue), long> _taggedTotals = [];

    public AuditMeterTotals()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "StepUpLogging.Audit")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            lock (_totals)
            {
                _totals[instrument.Name] = _totals.TryGetValue(instrument.Name, out var total) ? total + measurement : measurement;
                AddTagged(instrument.Name, tags, measurement);
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

    /// <summary>The sum of the measurements of <paramref name="instrumentName"/> that carried <paramref name="tagName"/>=<paramref name="tagValue"/>.</summary>
    public long Total(string instrumentName, string tagName, string tagValue)
    {
        lock (_totals)
        {
            return _taggedTotals.TryGetValue((instrumentName, tagName, tagValue), out var total) ? total : 0;
        }
    }

    /// <summary>Polls every enabled observable instrument (e.g. a gauge) once, recording its current value.</summary>
    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();

    private void AddTagged(string instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags, long measurement)
    {
        foreach (var tag in tags)
        {
            var key = (instrument, tag.Key, tag.Value?.ToString() ?? string.Empty);
            _taggedTotals[key] = _taggedTotals.GetValueOrDefault(key) + measurement;
        }
    }
}
