using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

internal static class LogProperties
{
    public const string IsRequestSummary = "IsRequestSummary";
    public const string IsImmediate = "IsImmediate";
    public const string Jti = "Jti";
    public const string Sid = "Sid";
    public const string ClientIp = "ClientIp";
    public const string RemoteIp = "RemoteIp";
    public const string RemotePort = "RemotePort";
    public const string QueryString = "QueryString";
    public const string RouteParameters = "RouteParameters";

    internal static bool HasFlag(LogEvent logEvent, string key)
        => logEvent.Properties.TryGetValue(key, out var v) && v is ScalarValue sv && sv.Value is bool b && b;
}
