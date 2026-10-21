namespace Lukdrasil.StepUpLogging;

// af-stub: passes every call through and writes no summary.
internal sealed class OutboundRequestSummaryHandler : DelegatingHandler
{
    internal OutboundRequestSummaryHandler(string clientName, StepUpLoggingController controller, CompiledRedactionPatterns patterns, RequestSummaryDetail detail)
    {
    }
}
