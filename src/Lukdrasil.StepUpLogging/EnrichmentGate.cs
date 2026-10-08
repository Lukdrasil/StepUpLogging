using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

internal sealed class EnrichmentGate(LoggingLevelSwitch levelSwitch, LogEventLevel stepUpLevel)
{
    public bool Needs(LogEvent logEvent) => logEvent.Level >= LogEventLevel.Verbose || levelSwitch.MinimumLevel >= stepUpLevel; // af-stub

    internal static Func<LogEvent, bool> For(
        LoggingLevelSwitch levelSwitch,
        LogEventLevel stepUpLevel,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        IConfiguration configuration)
        => _ => true; // af-stub
}
