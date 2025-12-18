using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

// Serilog sink that observes error-level events and triggers step-up logging without blocking the pipeline
internal sealed class StepUpTriggerSink : ILogEventSink, IDisposable
{
    private readonly StepUpLoggingController _controller;
    private readonly Channel<bool> _triggerChannel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts;

    private static readonly Meter Meter = new("StepUpLogging.Sink", "1.0.0");
    private static readonly Counter<long> ErrorEventsCounter = Meter.CreateCounter<long>("sink_error_events_total", "count", "Total number of error-level events observed");
    private static readonly Counter<long> DroppedEventsCounter = Meter.CreateCounter<long>("sink_dropped_events_total", "count", "Number of dropped events due to full channel");
    private static readonly Counter<long> ProcessedTriggersCounter = Meter.CreateCounter<long>("sink_processed_triggers_total", "count", "Number of triggers processed by background task");

    public StepUpTriggerSink(StepUpLoggingController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        _controller = controller;
        _triggerChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(ProcessTriggersAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Error)
        {
            ErrorEventsCounter.Add(1);
            if (!_triggerChannel.Writer.TryWrite(true))
            {
                DroppedEventsCounter.Add(1);
            }
        }
    }

    private async Task ProcessTriggersAsync()
    {
        try
        {
            await foreach (var _ in _triggerChannel.Reader.ReadAllAsync(_cts.Token))
            {
                _controller.Trigger();
                ProcessedTriggersCounter.Add(1);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public void Dispose()
    {
        _triggerChannel.Writer.Complete();
        _cts.Cancel();
        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }
        _cts.Dispose();
    }
}
