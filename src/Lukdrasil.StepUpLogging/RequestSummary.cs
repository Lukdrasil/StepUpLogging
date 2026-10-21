using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

// af-stub
internal sealed class RequestSummary
{
    internal RequestSummary(string direction, string method, string path, int? statusCode, double elapsedMs, string? rawUserAgent, CompiledRedactionPatterns patterns)
        => throw new NotImplementedException("af-stub");

    internal RequestSummary With(string name, long? value) => throw new NotImplementedException("af-stub");

    internal RequestSummary WithTrue(string name, bool value) => throw new NotImplementedException("af-stub");

    internal LogEvent ToLogEvent(LogEventLevel level, Serilog.ILogger logger) => throw new NotImplementedException("af-stub");
}
