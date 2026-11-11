using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SilentCollector = Lukdrasil.StepUpLogging.Tests.OtlpExportTimeoutTests.SilentCollector;

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
        var result = await RunDotnetAsync(Root, packagesPath: null,
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

    public Task<(int ExitCode, string Output, string StandardOutput)> RunConsumerDotnetAsync(string workingDirectory, params string[] arguments) =>
        RunDotnetAsync(workingDirectory, PackagesPath, arguments);

    public Task<(int ExitCode, string Output, string StandardOutput)> RunConsumerProcessAsync(
        string fileName, string workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] arguments) =>
        RunProcessAsync(fileName, workingDirectory, PackagesPath, environment, arguments);

    private static Task<(int ExitCode, string Output, string StandardOutput)> RunDotnetAsync(string workingDirectory, string? packagesPath, params string[] arguments) =>
        RunProcessAsync("dotnet", workingDirectory, packagesPath, new Dictionary<string, string>(), arguments);

    private static async Task<(int ExitCode, string Output, string StandardOutput)> RunProcessAsync(
        string fileName, string workingDirectory, string? packagesPath, IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
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

        if (packagesPath is not null)
        {
            startInfo.Environment["NUGET_PACKAGES"] = packagesPath;
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

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

    private const string AotConsumerProgram = """
        using System.Text;
        using System.Text.Json;
        using Lukdrasil.StepUpLogging;
        using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

        var port = Environment.GetEnvironmentVariable("SMOKE_PORT")!;
        var logs = Environment.GetEnvironmentVariable("SMOKE_LOGS")!;
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:0:Args:theme"] = "Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme::Code, Serilog.Sinks.Console",
            ["Serilog:WriteTo:1:Name"] = "File",
            ["Serilog:WriteTo:1:Args:path"] = Path.Combine(logs, "compact.json"),
            ["Serilog:WriteTo:1:Args:shared"] = "true",
            ["Serilog:WriteTo:1:Args:formatter"] = "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
            ["Serilog:WriteTo:2:Name"] = "File",
            ["Serilog:WriteTo:2:Args:path"] = Path.Combine(logs, "rendered-compact.json"),
            ["Serilog:WriteTo:2:Args:shared"] = "true",
            ["Serilog:WriteTo:2:Args:formatter"] = "Serilog.Formatting.Compact.RenderedCompactJsonFormatter, Serilog.Formatting.Compact",
            ["Serilog:WriteTo:3:Name"] = "File",
            ["Serilog:WriteTo:3:Args:path"] = Path.Combine(logs, "json.json"),
            ["Serilog:WriteTo:3:Args:shared"] = "true",
            ["Serilog:WriteTo:3:Args:formatter"] = "Serilog.Formatting.Json.JsonFormatter, Serilog",
            ["SerilogStepUp:EnableConsoleLogging"] = "false",
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, IdentityEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options =>
        {
            options.SpoolDirectory = Environment.GetEnvironmentVariable("SMOKE_SPOOL")!;
            options.EndpointBaseUrl = $"http://127.0.0.1:{port}";
            options.ModuleName = "smoke";
            options.Version = "1.0.0";
            options.DrainInterval = TimeSpan.FromSeconds(1);
        });

        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = builder.Build();
        app.UseStepUpRequestLogging();
        app.Map(new PathString("/audit"), branch => branch.Run(async context =>
        {
            using var envelope = await JsonDocument.ParseAsync(context.Request.Body);
            Console.WriteLine(Encoding.UTF8.GetString(envelope.RootElement.GetProperty("payload").GetBytesFromBase64()));
            delivered.TrySetResult();
            context.Response.StatusCode = StatusCodes.Status200OK;
        }));

        await app.StartAsync();
        using (var scope = app.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger<Program>>();
            await audit.AuditAsync(AuditEvent.Success("smoke.run", "u", "user") with { Data = new Dictionary<string, object?> { ["attempt"] = 1 } });
        }

        app.Logger.LogWarning("smoke-marker");
        var completed = await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(60))) == delivered.Task;
        await app.StopAsync();
        return completed ? 0 : 1;

        internal sealed class IdentityEncryptor : IAuditPayloadEncryptor
        {
            public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload) => ValueTask.FromResult(payload.ToArray());
        }
        """;

    private const string CliConsumerProgram = """
        using System.Diagnostics;
        using Lukdrasil.StepUpLogging;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using Microsoft.Extensions.Logging;

        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableConsoleLogging"] = "true",
            ["SerilogStepUp:ConsoleFormat"] = "Text",
            ["SerilogStepUp:WriteStartupSummary"] = "false",
        });
        builder.AddStepUpLogging();

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cli");
        using (new Activity("cli-command").Start())
        {
            logger.LogInformation("cli-held-back");
            logger.LogError("cli-marker");
        }

        return 0;
        """;

    [Fact]
    public async Task ConsumerOfOnlyThePackage_BuildsWithRuntimeGeneratorAndNoTelemetryAbstractionsAssets()
    {
        var consumer = WriteConsumer("only-package", extraPackageReferences: "");

        var build = await BuildAsync(consumer);

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.DoesNotContain("LOGGEN", build.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0757", build.Output, StringComparison.Ordinal);

        var nugetImports = Directory.EnumerateFiles(Path.Combine(consumer, "obj"), "*.nuget.g.*")
            .Where(file => file.EndsWith(".props", StringComparison.Ordinal) || file.EndsWith(".targets", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(nugetImports);

        foreach (var file in nugetImports)
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
            extraPackageReferences: $"""<PackageReference Include="{TelemetryAbstractions}" Version="11.0.0" />""");

        var build = await BuildAsync(consumer);

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.DoesNotContain("CS0757", build.Output, StringComparison.Ordinal);

        var analyzers = await AnalyzerItemsAsync(consumer);

        Assert.Contains(analyzers, analyzer => analyzer.PackageId == TelemetryAbstractions && analyzer.FileName == "Microsoft.Gen.Logging.dll");
    }

    [Fact]
    public async Task AotConsumer_WithSerilogSectionAndOneAuditRecord_PublishesWithoutLibraryWarningsAndRuns()
    {
        if (NativeAotToolchain.Current() is { } missingToolchain)
        {
            Assert.Skip(missingToolchain);
        }

        var consumer = WriteAotConsumer("aot-consumer", "Microsoft.NET.Sdk.Web", AotConsumerProgram);

        var publish = await package.RunConsumerDotnetAsync(consumer,
            "publish", "-c", "Release", "-o", "publish", "-p:StripSymbols=false", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false");

        Assert.True(publish.ExitCode == 0, publish.Output);
        var libraryWarnings = publish.Output.Split('\n')
            .Where(line => line.Contains("warning IL", StringComparison.Ordinal) && line.Contains("Lukdrasil.StepUpLogging", StringComparison.Ordinal))
            .ToList();
        Assert.True(libraryWarnings.Count == 0, string.Join(Environment.NewLine, libraryWarnings));

        var environment = new Dictionary<string, string>
        {
            ["SMOKE_PORT"] = FreeLoopbackPort().ToString(CultureInfo.InvariantCulture),
            ["SMOKE_SPOOL"] = Path.Combine(consumer, "spool"),
            ["SMOKE_LOGS"] = Path.Combine(consumer, "logs"),
        };
        var binary = Path.Combine(consumer, "publish", OperatingSystem.IsWindows() ? "AotConsumer.exe" : "AotConsumer");

        var run = await package.RunConsumerProcessAsync(binary, consumer, environment);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("smoke-marker", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"auditEvent\":{\"action\":\"smoke.run\"", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"data\":{\"attempt\":1}", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown configuration key", run.StandardOutput, StringComparison.Ordinal);

        foreach (var formatterFile in new[] { "compact.json", "rendered-compact.json", "json.json" })
        {
            Assert.Contains("smoke-marker", File.ReadAllText(Path.Combine(consumer, "logs", formatterFile)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AotCliConsumer_OnTheGenericHostWithASilentCollector_PublishesWithoutLibraryWarningsAndExitsWithinTheOtlpTimeout()
    {
        if (NativeAotToolchain.Current() is { } missingToolchain)
        {
            Assert.Skip(missingToolchain);
        }

        var consumer = WriteAotConsumer("aot-cli-consumer", "Microsoft.NET.Sdk", CliConsumerProgram);

        var publish = await package.RunConsumerDotnetAsync(consumer,
            "publish", "-c", "Release", "-o", "publish", "-p:StripSymbols=false", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false");

        Assert.True(publish.ExitCode == 0, publish.Output);
        var libraryWarnings = publish.Output.Split('\n')
            .Where(line => line.Contains("warning IL", StringComparison.Ordinal) && line.Contains("Lukdrasil.StepUpLogging", StringComparison.Ordinal))
            .ToList();
        Assert.True(libraryWarnings.Count == 0, string.Join(Environment.NewLine, libraryWarnings));

        using var collector = new SilentCollector();
        var environment = new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
            ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "500",
        };
        var binary = Path.Combine(consumer, "publish", OperatingSystem.IsWindows() ? "AotConsumer.exe" : "AotConsumer");

        var stopwatch = Stopwatch.StartNew();
        var run = await package.RunConsumerProcessAsync(binary, consumer, environment);
        stopwatch.Stop();

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(collector.Accepted > 0, "the native binary never connected to the collector");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"the native binary ran {stopwatch.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("cli-marker", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("cli-held-back", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("StepUp logging:", run.StandardOutput, StringComparison.Ordinal);
    }

    private string WriteConsumer(string name, string extraPackageReferences)
    {
        var directory = Path.Combine(package.Root, name);
        Directory.CreateDirectory(directory);

        WriteNuGetConfig(directory, package.FeedPath);

        File.WriteAllText(Path.Combine(directory, "PackTestConsumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
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

    private static void WriteNuGetConfig(string directory, string feedPath) =>
        File.WriteAllText(Path.Combine(directory, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="packtest" value="{feedPath}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

    private string WriteAotConsumer(string name, string sdk, string program)
    {
        var directory = Path.Combine(package.Root, name);
        Directory.CreateDirectory(directory);

        WriteNuGetConfig(directory, package.FeedPath);

        File.WriteAllText(Path.Combine(directory, "AotConsumer.csproj"), $"""
            <Project Sdk="{sdk}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net11.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <PublishAot>true</PublishAot>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Lukdrasil.StepUpLogging" Version="{package.Version}" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(directory, "Program.cs"), program);

        return directory;
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private Task<(int ExitCode, string Output, string StandardOutput)> BuildAsync(string consumer) =>
        package.RunConsumerDotnetAsync(consumer, "build", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false");

    private async Task<IReadOnlyList<(string FileName, string? PackageId)>> AnalyzerItemsAsync(string consumer)
    {
        var result = await package.RunConsumerDotnetAsync(consumer,
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
