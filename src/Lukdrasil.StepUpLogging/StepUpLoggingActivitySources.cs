using System.Diagnostics;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The library's activity sources. Consumers subscribe by the public names on <see cref="StepUpLoggingExtensions"/>;
/// the sources themselves stay internal so no one else starts activities under the library's name.
/// </summary>
internal static class StepUpLoggingActivitySources
{
    /// <summary>LogRequest, ApplyRedaction and CaptureRequestBody.</summary>
    internal static readonly ActivitySource RequestLogging = new(StepUpLoggingExtensions.RequestLoggingActivitySourceName, "1.0.0");

    /// <summary>TriggerStepUp and PerformStepDown.</summary>
    internal static readonly ActivitySource Controller = new(StepUpLoggingExtensions.ControllerActivitySourceName, "1.0.0");

    /// <summary>FlushBufferedEvents.</summary>
    internal static readonly ActivitySource Buffer = new(StepUpLoggingExtensions.BufferActivitySourceName, "1.0.0");
}
