using System.Reflection;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Applies the <c>Serilog</c> configuration section, with explicit reader assemblies only when
/// there is no dynamic code (ADR 0035 D1).
/// </summary>
internal static class SerilogConfigurationAssemblies
{
    /// <summary>The Serilog assemblies handed to the reader in a native image.</summary>
    /// <remarks>af-stub</remarks>
    internal static readonly IReadOnlyList<Assembly> ConfigurationLibraryAssemblies = [];

    /// <summary>Serilog assemblies the library references but deliberately leaves out, with the reason.</summary>
    /// <remarks>af-stub</remarks>
    internal static readonly IReadOnlyDictionary<string, string> DeliberateExclusions = new Dictionary<string, string>();

    /// <summary>Applies <paramref name="serilogConfig"/> to <paramref name="loggerConfiguration"/>.</summary>
    internal static LoggerConfiguration ReadSerilogSection(LoggerConfiguration loggerConfiguration, IConfiguration serilogConfig) =>
        throw new NotImplementedException("af-stub");

    /// <summary>Applies <paramref name="serilogConfig"/> as the runtime would with or without dynamic code.</summary>
    internal static LoggerConfiguration ReadSerilogSection(LoggerConfiguration loggerConfiguration, IConfiguration serilogConfig, bool dynamicCodeSupported) =>
        throw new NotImplementedException("af-stub");
}
