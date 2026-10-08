using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using static Lukdrasil.StepUpLogging.Tests.TestHosts;

namespace Lukdrasil.StepUpLogging.Tests;

public class EnrichmentGateHostTests
{
    private const string Application = "Application";
    private const string AlwaysCategory = "Test.Always";

    [Fact]
    public void RootFilterProbe_SeesDebugWithoutApplication_WarningWithIt()
    {
        var probeKey = Guid.NewGuid().ToString("N");
        try
        {
            Run(
                new()
                {
                    ["Serilog:Filter:0:Name"] = nameof(KeyedCaptureSink.RootProbe),
                    ["Serilog:Filter:0:Args:key"] = probeKey,
                },
                configure: null,
                (services, _) =>
                {
                    var logger = services.GetRequiredService<Serilog.ILogger>();
                    logger.Debug("{Token}", "GATE_PROBE_DEBUG");
                    logger.Warning("{Token}", "GATE_PROBE_WARN");
                });

            Assert.DoesNotContain(Application, Probed(probeKey, "GATE_PROBE_DEBUG").Properties.Keys);
            Assert.Contains(Application, Probed(probeKey, "GATE_PROBE_WARN").Properties.Keys);
        }
        finally
        {
            KeyedCaptureSink.Forget(probeKey);
        }
    }

    [Fact]
    public void ManualSwitchDebug_DebugExportedWithApplication()
    {
        var exported = Run([], configure: null, (services, _) =>
        {
            services.GetRequiredService<StepUpLoggingController>().LevelSwitch.MinimumLevel = LogEventLevel.Debug;
            services.GetRequiredService<Serilog.ILogger>().Debug("{Token}", "GATE_MANUAL_DEBUG");
        });

        Assert.Contains(Application, Single(exported, "GATE_MANUAL_DEBUG").Properties.Keys);
    }

    [Fact]
    public void Diagnostic_DebugExportedWithApplication()
    {
        var exported = Run(
            new() { ["SerilogStepUp:Mode"] = "Diagnostic", ["SerilogStepUp:DiagnosticLevel"] = "Debug" },
            configure: null,
            (services, _) => services.GetRequiredService<Serilog.ILogger>().Debug("{Token}", "GATE_DIAGNOSTIC_DEBUG"));

        Assert.Contains(Application, Single(exported, "GATE_DIAGNOSTIC_DEBUG").Properties.Keys);
    }

    [Fact]
    public void SteppedUp_InformationExportedWithApplication()
    {
        var exported = Run([], configure: null, (services, _) =>
        {
            StepUp(services.GetRequiredService<StepUpLoggingController>());
            services.GetRequiredService<Serilog.ILogger>().Information("{Token}", "GATE_STEPPED_INFO");
        });

        Assert.Contains(Application, Single(exported, "GATE_STEPPED_INFO").Properties.Keys);
    }

    [Fact]
    public void HeldInformation_FlushedOnceByError()
    {
        var exported = Run([], configure: null, (services, captureKey) =>
        {
            var logger = services.GetRequiredService<Serilog.ILogger>();
            logger.Information("{Token}", "GATE_HELD_INFO");
            Assert.Equal(0, KeyedCaptureSink.Count(captureKey, "GATE_HELD_INFO"));

            logger.Error("{Token}", "GATE_HELD_ERR");
        });

        Assert.Contains(Application, Single(exported, "GATE_HELD_INFO").Properties.Keys);
    }

    [Fact]
    public void LogImmediateDebugViaMel_ExportedOnce()
    {
        var exported = Run([], configure: null, (services, _) =>
            MelLogger(services).LogImmediate(LogLevel.Debug, "{Token}", "GATE_IMMEDIATE_MEL_DEBUG"));

        Single(exported, "GATE_IMMEDIATE_MEL_DEBUG");
    }

    [Fact]
    public void BeginImmediateScopeVerbose_ExportedOnce()
    {
        var exported = Run([], configure: null, (services, _) =>
        {
            var logger = MelLogger(services);
            using (logger.BeginImmediateScope())
            {
                logger.LogTrace("{Token}", "GATE_IMMEDIATE_SCOPE_TRACE");
            }
        });

        Single(exported, "GATE_IMMEDIATE_SCOPE_TRACE");
    }

    [Fact]
    public void AlwaysExportDebug_ExportedOnce()
    {
        var exported = Run(
            new() { ["SerilogStepUp:AlwaysExportCategories:0"] = AlwaysCategory },
            configure: null,
            (services, _) => services.GetRequiredService<Serilog.ILogger>()
                .ForContext("SourceContext", AlwaysCategory)
                .Debug("{Token}", "GATE_ALWAYS_DEBUG"));

        Single(exported, "GATE_ALWAYS_DEBUG");
    }

    [Fact]
    public void RequestSummaryDebug_ExportedOnce()
    {
        var exported = Run([], configure: null, (services, _) =>
            services.GetRequiredService<Serilog.ILogger>()
                .ForContext(LogProperties.IsRequestSummary, true)
                .Debug("{Token}", "GATE_SUMMARY_DEBUG"));

        Single(exported, "GATE_SUMMARY_DEBUG");
    }

    [Fact]
    public void ConfigureHookRootSink_SeesDebugWithApplication()
    {
        var hookKey = Guid.NewGuid().ToString("N");
        try
        {
            Run(
                [],
                (_, lc) => lc.WriteTo.StepUpCapture(hookKey),
                (services, _) => services.GetRequiredService<Serilog.ILogger>().Debug("{Token}", "GATE_HOOK_DEBUG"));

            Assert.Contains(Application, Single(KeyedCaptureSink.Events(hookKey), "GATE_HOOK_DEBUG").Properties.Keys);
        }
        finally
        {
            KeyedCaptureSink.Forget(hookKey);
        }
    }

    [Fact]
    public void AuditToSink_SeesDebugWithApplication()
    {
        var auditKey = Guid.NewGuid().ToString("N");
        try
        {
            Run(
                new()
                {
                    ["Serilog:AuditTo:0:Name"] = nameof(KeyedCaptureSink.StepUpAuditCapture),
                    ["Serilog:AuditTo:0:Args:key"] = auditKey,
                },
                configure: null,
                (services, _) => services.GetRequiredService<Serilog.ILogger>().Debug("{Token}", "GATE_AUDITTO_DEBUG"));

            Assert.Contains(Application, Single(KeyedCaptureSink.Events(auditKey), "GATE_AUDITTO_DEBUG").Properties.Keys);
        }
        finally
        {
            KeyedCaptureSink.Forget(auditKey);
        }
    }

    [Fact]
    public void WarningExport_CarriesCommonProperties()
    {
        var exported = Run(
            new()
            {
                ["SerilogStepUp:ServiceVersion"] = "9.9.9",
                ["SerilogStepUp:ServiceInstanceId"] = "instance-1",
                ["SerilogStepUp:EnrichWithThreadId"] = "true",
            },
            configure: null,
            (services, _) => services.GetRequiredService<Serilog.ILogger>().Warning("{Token}", "GATE_COMMON_WARN"));

        var keys = Single(exported, "GATE_COMMON_WARN").Properties.Keys;
        Assert.All(
            new[] { Application, "Environment", "MachineName", "ServiceVersion", "ServiceInstanceId", "ThreadId" },
            property => Assert.Contains(property, keys));
    }

    private static Microsoft.Extensions.Logging.ILogger MelLogger(IServiceProvider services)
        => services.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Gate");

    private static LogEvent Single(LogEvent[] events, string token)
        => Assert.Single(events, e => e.RenderMessage().Contains(token, StringComparison.Ordinal));

    private static LogEvent Probed(string probeKey, string token)
        => Single(KeyedCaptureSink.Events(probeKey), token);

    private static LogEvent[] Run(
        Dictionary<string, string?> overrides,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        Action<IServiceProvider, string> scenario)
    {
        var captureKey = Guid.NewGuid().ToString("N");
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "true",
                ["SerilogStepUp:Mode"] = "Auto",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["SerilogStepUp:DurationSeconds"] = "300",
                ["Logging:LogLevel:Default"] = "Trace",
                ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
                ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
                ["Serilog:WriteTo:0:Args:key"] = captureKey,
            };
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.AddStepUpLogging(configureOptions: null, configure: configure);

            using var activity = new Activity("enrichment-gate");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            using (var host = builder.Build())
            {
                scenario(host.Services, captureKey);
            }

            return KeyedCaptureSink.Events(captureKey);
        }
        finally
        {
            KeyedCaptureSink.Forget(captureKey);
        }
    }
}
