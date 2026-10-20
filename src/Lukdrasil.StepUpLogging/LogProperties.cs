using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

internal static class LogProperties
{
    public const string IsRequestSummary = "IsRequestSummary";
    public const string IsImmediate = "IsImmediate";
    public const string Jti = "Jti";

    internal static bool HasFlag(LogEvent logEvent, string key)
        => logEvent.Properties.TryGetValue(key, out var v) && v is ScalarValue sv && sv.Value is bool b && b;
}
