using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using static Lukdrasil.StepUpLogging.Tests.TestHosts;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>Redirects the process-wide <see cref="Console.Out"/>, so it runs alone.</summary>
[Collection(ConsoleOutputCollection.Name)]
public class ConsoleOutputTests
{
    [Fact]
    public void ConsoleText_Traceparent_WritesTheConfiguredLineToStandardOutput()
    {
        var builder = NewBuilder();
        builder.AddStepUpLogging(opts =>
        {
            opts.EnableConsoleLogging = true;
            opts.ConsoleFormat = ConsoleLogFormat.Text;
            opts.ConsoleTraceFormat = ConsoleTraceFormat.Traceparent;
            opts.ConsoleTextProperties = ["Jti"];
        });
        var output = new StringWriter();
        var original = Console.Out;
        using var activity = new Activity("console-probe");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        Console.SetOut(output);
        try
        {
            using (var host = builder.Build())
            {
                host.Services.GetRequiredService<Serilog.ILogger>().ForContext("Jti", "j-42").Warning("console probe {Order}", 7);
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        var line = Assert.Single(output.ToString().Split(Environment.NewLine), l => l.Contains("console probe", StringComparison.Ordinal));
        Assert.Matches(
            "^Timestamp=\\d{2}:\\d{2}:\\d{2}\\.\\d{3} " + Regex.Escape(
                $"Level=WRN traceparent=00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-01 Message=\"console probe 7\" Jti=j-42") + "$",
            line);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Console output";
}
