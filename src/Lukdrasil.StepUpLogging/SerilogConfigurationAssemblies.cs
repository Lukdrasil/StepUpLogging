using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Settings.Configuration;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Applies the <c>Serilog</c> configuration section, with explicit reader assemblies only when
/// there is no dynamic code (ADR 0035 D1).
/// </summary>
internal static class SerilogConfigurationAssemblies
{
    /// <summary>
    /// The Serilog assemblies handed to the reader in a native image. Every Serilog assembly the
    /// library's compiled assembly references is either here or in <see cref="DeliberateExclusions"/>,
    /// never both, and <c>SerilogConfigurationAssembliesTests</c> enforces it. The measure is compiled
    /// references, so a Serilog package used only from configuration has to be added by hand.
    /// </summary>
    internal static readonly IReadOnlyList<Assembly> ConfigurationLibraryAssemblies =
    [
        typeof(Log).Assembly,
        typeof(ConsoleLoggerConfigurationExtensions).Assembly,
        typeof(FileLoggerConfigurationExtensions).Assembly,
        typeof(LoggerConfigurationAsyncExtensions).Assembly,
        typeof(OpenTelemetryLoggerConfigurationExtensions).Assembly,
        typeof(ThreadLoggerConfigurationExtensions).Assembly,
        typeof(EnvironmentLoggerConfigurationExtensions).Assembly,
        typeof(ProcessLoggerConfigurationExtensions).Assembly,
        typeof(Serilog.Enrichers.OpenTelemetry.LoggingExtensions).Assembly,
        typeof(Serilog.Formatting.Compact.CompactJsonFormatter).Assembly,
    ];

    /// <summary>
    /// Serilog assemblies the library references but deliberately leaves out of
    /// <see cref="ConfigurationLibraryAssemblies"/>, with the reason; record a new exclusion here.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DeliberateExclusions = new Dictionary<string, string>
    {
        ["Serilog.Settings.Configuration"] = "the reader itself; no sink, enricher or formatter",
        ["Serilog.Extensions.Hosting"] = "host integration; nothing the Serilog section can name",
        ["Serilog.AspNetCore"] = "request-logging middleware; nothing the Serilog section can name",
        ["Serilog.Exceptions"] = "reflection-based with trim warnings; use the EnrichWithExceptionDetails option",
        ["Serilog.Enrichers.CallStack"] = "reflection-based with trim warnings; use the EnrichWithCallStack option",
    };

    /// <summary>Applies <paramref name="serilogConfig"/> to <paramref name="loggerConfiguration"/>.</summary>
    internal static LoggerConfiguration ReadSerilogSection(LoggerConfiguration loggerConfiguration, IConfiguration serilogConfig) =>
        ReadSerilogSection(loggerConfiguration, serilogConfig, RuntimeFeature.IsDynamicCodeSupported);

    /// <summary>Applies <paramref name="serilogConfig"/> as the runtime would with or without dynamic code.</summary>
    internal static LoggerConfiguration ReadSerilogSection(LoggerConfiguration loggerConfiguration, IConfiguration serilogConfig, bool dynamicCodeSupported) =>
        dynamicCodeSupported
            ? loggerConfiguration.ReadFrom.Configuration(serilogConfig)
            : loggerConfiguration.ReadFrom.Configuration(serilogConfig, ReaderOptions(serilogConfig));

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ConsoleLoggerConfigurationExtensions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(FileLoggerConfigurationExtensions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(LoggerConfigurationAsyncExtensions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(OpenTelemetryLoggerConfigurationExtensions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ThreadLoggerConfigurationExtensions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(EnvironmentLoggerConfigurationExtensions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ProcessLoggerConfigurationExtensions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(Serilog.Enrichers.OpenTelemetry.LoggingExtensions))]
    private static ConfigurationReaderOptions ReaderOptions(IConfiguration serilogConfig) =>
        new([.. ConfigurationLibraryAssemblies.Concat(UsingAssemblies(serilogConfig)).Distinct()]);

    private static IEnumerable<Assembly> UsingAssemblies(IConfiguration serilogConfig) =>
        serilogConfig.GetSection("Serilog:Using").GetChildren()
            .Select(section => section.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Assembly.Load(new AssemblyName(name!)));
}
