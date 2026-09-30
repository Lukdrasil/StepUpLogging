using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Tests;

public class PackagingHookTests(PackedStepUpLoggingPackage package) : IClassFixture<PackedStepUpLoggingPackage>
{
    [Fact]
    public async Task ConsumerResolvingPackageAssetsFirst_StillHasNoTelemetryAbstractionsAnalyzers()
    {
        var consumer = Path.Combine(package.Root, "resolve-assets-first");
        Directory.CreateDirectory(consumer);

        File.WriteAllText(Path.Combine(consumer, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="packtest" value="{package.FeedPath}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        File.WriteAllText(Path.Combine(consumer, "PackTestConsumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Lukdrasil.StepUpLogging" Version="{package.Version}" />
              </ItemGroup>
            </Project>
            """);

        var result = await package.RunConsumerDotnetAsync(consumer,
            "msbuild", "-restore", "-t:ResolvePackageAssets;Build", "-getItem:Analyzer", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false");

        Assert.True(result.ExitCode == 0, result.Output);

        using var json = JsonDocument.Parse(result.StandardOutput);
        var packageIds = json.RootElement.GetProperty("Items").GetProperty("Analyzer").EnumerateArray()
            .Select(item => item.TryGetProperty("NuGetPackageId", out var id) ? id.GetString() : null);

        Assert.DoesNotContain("Microsoft.Extensions.Telemetry.Abstractions", packageIds);
    }

    [Fact]
    public async Task LibraryProject_DeclaresShippedTargetsAsOneNoneItem()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Lukdrasil.StepUpLogging.slnx")))
        {
            root = root.Parent!;
        }

        var project = Path.Combine(root.FullName, "src", "Lukdrasil.StepUpLogging", "Lukdrasil.StepUpLogging.csproj");

        var result = await package.RunConsumerDotnetAsync(package.Root, "msbuild", project, "-getItem:None", "-nologo");

        Assert.True(result.ExitCode == 0, result.Output);

        using var json = JsonDocument.Parse(result.StandardOutput);
        var targets = json.RootElement.GetProperty("Items").GetProperty("None").EnumerateArray()
            .Count(item => item.GetProperty("FullPath").GetString()!.EndsWith("Lukdrasil.StepUpLogging.targets", StringComparison.Ordinal));

        Assert.Equal(1, targets);
    }
}
