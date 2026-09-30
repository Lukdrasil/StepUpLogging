using System.Diagnostics;
using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Tests;

public sealed class PackedStepUpLoggingPackage : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "stepup-packtest", Guid.NewGuid().ToString("N"));

    public string Version { get; } = $"0.0.0-packtest{Guid.NewGuid():N}";

    public string FeedPath => Path.Combine(Root, "feed");

    public string PackagesPath => Path.Combine(Root, "packages");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(FeedPath);
        Directory.CreateDirectory(PackagesPath);

        var project = Path.Combine(FindRepositoryRoot(), "src", "Lukdrasil.StepUpLogging", "Lukdrasil.StepUpLogging.csproj");
        var result = await RunDotnetAsync(Root,
            "build", project, "-c", "PackTest", $"-p:Version={Version}", $"-p:PackageOutputPath={FeedPath}",
            "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false");

        Assert.True(result.ExitCode == 0, $"PackTest build of the library failed:{Environment.NewLine}{result.Output}");
        Assert.True(File.Exists(Path.Combine(FeedPath, $"Lukdrasil.StepUpLogging.{Version}.nupkg")), result.Output);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    public async Task<(int ExitCode, string Output, string StandardOutput)> RunDotnetAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var key in startInfo.Environment.Keys.Where(key => key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["NUGET_PACKAGES"] = PackagesPath;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var standardOutput = await stdout;
        return (process.ExitCode, standardOutput + await stderr, standardOutput);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lukdrasil.StepUpLogging.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No Lukdrasil.StepUpLogging.slnx above {AppContext.BaseDirectory}.");
    }
}

public class PackageConsumerBuildTests(PackedStepUpLoggingPackage package) : IClassFixture<PackedStepUpLoggingPackage>
{
    private const string TelemetryAbstractions = "Microsoft.Extensions.Telemetry.Abstractions";

    private const string LoggerMessageSource = """
        using Microsoft.Extensions.Logging;

        namespace PackTestConsumer;

        public static partial class Log
        {
            [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} shipped")]
            public static partial void OrderShipped(ILogger logger, int orderId);
        }
        """;

    [Fact]
    public async Task ConsumerOfOnlyThePackage_BuildsWithRuntimeGeneratorAndNoTelemetryAbstractionsAssets()
    {
        var consumer = WriteConsumer("only-package", extraPackageReferences: "");

        var build = await BuildAsync(consumer);

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.DoesNotContain("LOGGEN", build.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0757", build.Output, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(consumer, "obj"), "*.nuget.g.*")
                     .Where(file => file.EndsWith(".props", StringComparison.Ordinal) || file.EndsWith(".targets", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(TelemetryAbstractions, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }

        var analyzers = await AnalyzerItemsAsync(consumer);

        Assert.DoesNotContain(analyzers, analyzer => analyzer.PackageId == TelemetryAbstractions);
        Assert.Contains(analyzers, analyzer => analyzer.FileName == "Microsoft.Extensions.Logging.Generators.dll");
    }

    [Fact]
    public async Task ConsumerAlsoReferencingTelemetryAbstractions_BuildsAndKeepsGenLogging()
    {
        var consumer = WriteConsumer(
            "with-telemetry-abstractions",
            extraPackageReferences: $"""<PackageReference Include="{TelemetryAbstractions}" Version="10.0.0" />""");

        var build = await BuildAsync(consumer);

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.DoesNotContain("CS0757", build.Output, StringComparison.Ordinal);

        var analyzers = await AnalyzerItemsAsync(consumer);

        Assert.Contains(analyzers, analyzer => analyzer.PackageId == TelemetryAbstractions && analyzer.FileName == "Microsoft.Gen.Logging.dll");
    }

    private string WriteConsumer(string name, string extraPackageReferences)
    {
        var directory = Path.Combine(package.Root, name);
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="packtest" value="{package.FeedPath}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        File.WriteAllText(Path.Combine(directory, "PackTestConsumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Lukdrasil.StepUpLogging" Version="{package.Version}" />
                {extraPackageReferences}
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(directory, "Log.cs"), LoggerMessageSource);

        return directory;
    }

    private Task<(int ExitCode, string Output, string StandardOutput)> BuildAsync(string consumer) =>
        package.RunDotnetAsync(consumer, "build", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false");

    private async Task<IReadOnlyList<(string FileName, string? PackageId)>> AnalyzerItemsAsync(string consumer)
    {
        var result = await package.RunDotnetAsync(consumer,
            "msbuild", "-t:Build", "-getItem:Analyzer", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false");

        Assert.True(result.ExitCode == 0, result.Output);

        using var json = JsonDocument.Parse(result.StandardOutput);
        return
        [
            .. json.RootElement.GetProperty("Items").GetProperty("Analyzer").EnumerateArray().Select(item => (
                Path.GetFileName(item.GetProperty("Identity").GetString()!),
                item.TryGetProperty("NuGetPackageId", out var id) ? id.GetString() : null)),
        ];
    }
}
