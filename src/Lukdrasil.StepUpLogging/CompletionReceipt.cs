using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Tells the request summary middleware whether the request's completion event carried the summary and was exported,
/// so the summary is omitted only then (ADR 0043 D3). The middleware issues one receipt per request under
/// <c>RequestSummaryOnError=Omit</c>; it flows to the completion event's enricher and to the root sinks through an
/// <see cref="AsyncLocal{T}"/>, because Serilog.AspNetCore writes that event inside the middleware's
/// <c>await next()</c> and the root sinks run synchronously in that write. The sinks that hand an event to an output
/// logger (<see cref="StepUpSink"/> and <see cref="BypassSink"/>) call <see cref="NoteExported"/>; a sink that does
/// not, or an event dropped before it, leaves the summary to be written.
/// </summary>
internal sealed class CompletionReceipt
{
    /// <summary>The <c>SourceContext</c> of the request completion event, which Serilog.AspNetCore writes.</summary>
    internal const string SourceContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private static readonly AsyncLocal<CompletionReceipt?> Open = new();

    private bool _carried;
    private bool _exported;

    /// <summary>The receipt of the current request, or <see langword="null"/> when none was issued.</summary>
    internal static CompletionReceipt? Current => Open.Value;

    /// <summary>Whether the completion event carried the summary and an output logger was handed it.</summary>
    internal bool SummaryOmitted => _carried && _exported;

    /// <summary>Opens a receipt for the request running in the current execution context.</summary>
    internal static CompletionReceipt Issue()
    {
        var receipt = new CompletionReceipt();
        Open.Value = receipt;
        return receipt;
    }

    /// <summary>Records that the completion event carries the summary's fields.</summary>
    internal void MarkCarried() => _carried = true;

    /// <summary>Records the export of <paramref name="logEvent"/> when it is the completion event that carries the summary.</summary>
    internal static void NoteExported(LogEvent logEvent)
    {
        if (Open.Value is { _carried: true } receipt && IsCompletion(logEvent))
        {
            receipt._exported = true;
        }
    }

    private static bool IsCompletion(LogEvent logEvent)
        => logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: SourceContext };
}
