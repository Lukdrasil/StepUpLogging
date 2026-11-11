using Microsoft.Extensions.Configuration;
using Serilog;

namespace Lukdrasil.StepUpLogging.Tests;

public class SerilogConfigurationAssembliesTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> FileSinkSection(string path) => new()
    {
        ["Serilog:WriteTo:0:Name"] = "File",
        ["Serilog:WriteTo:0:Args:path"] = path,
    };

    private static void LogThrough(LoggerConfiguration loggerConfiguration, string marker)
    {
        using var logger = loggerConfiguration.CreateLogger();
        logger.Information(marker);
    }

    private static string NewMarker() => $"section-marker-{Guid.NewGuid():N}";

    [Fact]
    public void ConfigurationLibraryAssemblies_AreTheReferencedSerilogAssembliesMinusTheDeliberateExclusions()
    {
        var referenced = typeof(SerilogConfigurationAssemblies).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name!)
            .Where(name => name.StartsWith("Serilog", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        var table = SerilogConfigurationAssemblies.ConfigurationLibraryAssemblies.Select(assembly => assembly.GetName().Name!).ToList();
        var excluded = SerilogConfigurationAssemblies.DeliberateExclusions.Keys.ToList();

        Assert.Equal(referenced, table.Concat(excluded).Order(StringComparer.Ordinal).ToList());
        Assert.Empty(table.Intersect(excluded, StringComparer.Ordinal));
    }

    [Fact]
    public void ReadSerilogSection_UnderJit_FileSinkWithoutUsing_WritesTheEvent()
    {
        using var directory = new TempSpoolDirectory();
        var path = Path.Combine(directory.FullPath, "section.log");
        var marker = NewMarker();

        LogThrough(SerilogConfigurationAssemblies.ReadSerilogSection(new LoggerConfiguration(), Config(FileSinkSection(path))), marker);

        Assert.Contains(marker, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSerilogSection_WithoutDynamicCode_FileSinkWithoutUsing_WritesTheEvent()
    {
        using var directory = new TempSpoolDirectory();
        var path = Path.Combine(directory.FullPath, "section.log");
        var marker = NewMarker();

        LogThrough(SerilogConfigurationAssemblies.ReadSerilogSection(new LoggerConfiguration(), Config(FileSinkSection(path)), dynamicCodeSupported: false), marker);

        Assert.Contains(marker, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSerilogSection_WithoutDynamicCode_UsingNamesTheTestAssembly_ResolvesItsSink()
    {
        var key = Guid.NewGuid().ToString("N");
        var marker = NewMarker();
        var config = Config(new()
        {
            ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
            ["Serilog:WriteTo:0:Name"] = "StepUpCapture",
            ["Serilog:WriteTo:0:Args:key"] = key,
        });

        try
        {
            LogThrough(SerilogConfigurationAssemblies.ReadSerilogSection(new LoggerConfiguration(), config, dynamicCodeSupported: false), marker);

            Assert.Equal(1, KeyedCaptureSink.Count(key, marker));
        }
        finally
        {
            KeyedCaptureSink.Forget(key);
        }
    }

    [Fact]
    public void ReadSerilogSection_WithoutDynamicCode_TestSinkWithoutUsing_IsNotResolved()
    {
        var key = Guid.NewGuid().ToString("N");
        var marker = NewMarker();
        var config = Config(new()
        {
            ["Serilog:WriteTo:0:Name"] = "StepUpCapture",
            ["Serilog:WriteTo:0:Args:key"] = key,
        });

        try
        {
            LogThrough(SerilogConfigurationAssemblies.ReadSerilogSection(new LoggerConfiguration(), config, dynamicCodeSupported: false), marker);

            Assert.Equal(0, KeyedCaptureSink.Count(key, marker));
        }
        finally
        {
            KeyedCaptureSink.Forget(key);
        }
    }

    [Fact]
    public void ReadSerilogSection_WithoutDynamicCode_BlankUsingEntry_IsSkipped()
    {
        using var directory = new TempSpoolDirectory();
        var path = Path.Combine(directory.FullPath, "section.log");
        var marker = NewMarker();
        var section = FileSinkSection(path);
        section["Serilog:Using:0"] = "  ";

        LogThrough(SerilogConfigurationAssemblies.ReadSerilogSection(new LoggerConfiguration(), Config(section), dynamicCodeSupported: false), marker);

        Assert.Contains(marker, File.ReadAllText(path), StringComparison.Ordinal);
    }
}
