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
    private readonly StepUpLoggingController _controller;
    private readonly Channel<bool> _triggerChannel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

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
        if (_disposed)
        {
            return; // Ignore emissions after disposal
        }

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

    /// <summary>
    /// Asynchronously disposes the sink, ensuring the background processing task completes gracefully.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _triggerChannel.Writer.Complete();
        _cts.Cancel();

        try
        {
            // Give background task time to complete gracefully
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception)
        {
            // Suppress other exceptions during disposal
        }
        finally
        {
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Synchronous disposal fallback (converts to async internally).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _triggerChannel.Writer.Complete();
        _cts.Cancel();

        try
        {
            // Best-effort sync wait with timeout for disposal
            if (!_processingTask.IsCompleted && !_processingTask.Wait(TimeSpan.FromSeconds(2)))
            {
                // Task did not complete in time - will eventually complete on app shutdown
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception)
        {
            // Suppress exceptions during synchronous disposal
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
