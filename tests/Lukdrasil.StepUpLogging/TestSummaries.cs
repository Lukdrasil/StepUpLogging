namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>Inbound request summaries built from already redacted field values, and the writer that writes them.</summary>
internal static class TestSummaries
{
    private static readonly CompiledRedactionPatterns NoPatterns = new([]);

    internal static RequestSummary Inbound(
        string method,
        string path,
        int statusCode,
        double elapsedMs,
        string? queryString = null,
        IReadOnlyDictionary<string, object?>? routeParameters = null,
        string? userAgent = null,
        string? clientIp = null,
        string? jti = null,
        string? forwardedFor = null)
        => new RequestSummary(RequestSummary.Inbound, method, path, statusCode, elapsedMs, userAgent, NoPatterns)
            .With("ClientIp", clientIp)
            .With(LogProperties.Jti, jti)
            .With("QueryString", queryString)
            .With("RouteParameters", routeParameters)
            .With("ForwardedFor", forwardedFor);

    /// <summary>A writer over <paramref name="logger"/> with <paramref name="options"/>, defaults applied, and no redaction patterns.</summary>
    internal static RequestSummaryWriter Writer(Serilog.ILogger logger, StepUpLoggingOptions? options = null)
    {
        options ??= new StepUpLoggingOptions();
        StepUpSettings.ApplyDefaults(options);
        return new RequestSummaryWriter(new StepUpSettings(options), NoPatterns, () => logger);
    }
}
