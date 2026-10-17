using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// A built-in framework category rule: the <c>SourceContext</c> prefix, its floor, and whether its events below
/// <c>Warning</c> stay in the pre-error buffer.
/// </summary>
internal readonly record struct FrameworkCategoryRule(string Prefix, LogEventLevel Floor, bool KeptInBuffer);

/// <summary>
/// The rules of <see cref="FrameworkCategoryPreset.Recommended"/> (ADR 0029).
/// </summary>
internal static class FrameworkCategoryDefaultTable
{
    public static readonly FrameworkCategoryRule[] Recommended =
    [
        new("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning, KeptInBuffer: false),
        new("Microsoft.AspNetCore.Routing", LogEventLevel.Warning, KeptInBuffer: false),
        new("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning, KeptInBuffer: false),
        new("Microsoft.AspNetCore.Http.HttpResults", LogEventLevel.Warning, KeptInBuffer: false),
        new("Microsoft.AspNetCore.StaticFiles", LogEventLevel.Warning, KeptInBuffer: false),
        new("Microsoft.AspNetCore.Cors", LogEventLevel.Warning, KeptInBuffer: true),
        new("System.Net.Http.HttpClient", LogEventLevel.Warning, KeptInBuffer: false),
        new("Polly", LogEventLevel.Warning, KeptInBuffer: false),
        new("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning, KeptInBuffer: false),
        new("Microsoft.EntityFrameworkCore.Infrastructure", LogEventLevel.Warning, KeptInBuffer: false),
        new("Npgsql.Command", LogEventLevel.Warning, KeptInBuffer: false),
        new("Yarp.ReverseProxy.Forwarder", LogEventLevel.Warning, KeptInBuffer: true),
    ];
}
