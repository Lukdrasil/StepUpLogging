using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Serilog sink that observes error-level events and triggers step-up logging without blocking the pipeline.
/// Implements proper async disposal to prevent memory leaks from background task.
/// </summary>
internal sealed class StepUpTriggerSink : ILogEventSink, IAsyncDisposable, IDisposable
{
    private readonly Action _trigger;
    private readonly CategoryPolicy? _categories;
    private readonly Channel<bool> _triggerChannel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    private static readonly Meter Meter = new("StepUpLogging.Sink", "1.0.0");
    private static readonly Counter<long> ErrorEventsCounter = Meter.CreateCounter<long>("sink_error_events_total", "count", "Number of error-level events that requested a step-up trigger");
    private static readonly Counter<long> DroppedEventsCounter = Meter.CreateCounter<long>("sink_dropped_events_total", "count", "Number of dropped events due to full channel");
    private static readonly Counter<long> ProcessedTriggersCounter = Meter.CreateCounter<long>("sink_processed_triggers_total", "count", "Number of triggers processed by background task");
    private static readonly Counter<long> FailedTriggersCounter = Meter.CreateCounter<long>("sink_failed_triggers_total", "count", "Number of triggers that threw and were skipped");

    public StepUpTriggerSink(StepUpLoggingController controller, CategoryPolicy? categories = null)
        : this(RequireTrigger(controller), categories)
    {
    }

    /// <summary>
    /// Internal seam allowing tests to substitute a trigger delegate that can fail independently
    /// of a real <see cref="StepUpLoggingController"/>.
    /// </summary>
    internal StepUpTriggerSink(Action trigger, CategoryPolicy? categories = null)
    {
        _trigger = trigger;
        _categories = categories;
        _triggerChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(ProcessTriggersAsync);
    }

    private static Action RequireTrigger(StepUpLoggingController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.Trigger;
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed)
        {
            return; // Ignore emissions after disposal
        }

        if (logEvent.Level >= LogEventLevel.Error && _categories?.NeverTriggers(logEvent) != true)
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
                try
                {
                    _trigger();
                    ProcessedTriggersCounter.Add(1);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A failing trigger must not kill the background loop; later triggers still need processing.
                    FailedTriggersCounter.Add(1);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    /// <summary>
    /// Asynchronously disposes the sink, ensuring the background processing task completes gracefully.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
        {
            return;
        }

        await _processingTask.ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>
    /// Synchronous disposal: waits at most two seconds for the background task, which ends on its own once its
    /// token is cancelled.
    /// </summary>
    public void Dispose()
    {
        if (!BeginDispose())
        {
            return;
        }

        _processingTask.Wait(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }

    /// <summary>
    /// Stops accepting events and cancels the background task; <see langword="false"/> when already disposed. The task
    /// never faults: it handles its own cancellation and every trigger failure, so awaiting it cannot throw.
    /// </summary>
    private bool BeginDispose()
    {
        if (_disposed)
        {
            return false;
        }

        _disposed = true;
        _triggerChannel.Writer.Complete();
        _cts.Cancel();
        return true;
    }
}
