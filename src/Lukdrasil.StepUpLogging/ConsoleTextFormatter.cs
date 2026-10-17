using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The <see cref="ConsoleLogFormat.Text"/> console line: time, level, trace id, <c>SourceContext</c> and the rendered
/// message, then each listed property the event has as <c>Name=value</c>, then the exception on the following lines.
/// </summary>
internal sealed class ConsoleTextFormatter(string[] properties) : ITextFormatter
{
    private static readonly MessageTemplateTextFormatter Line =
        new("[{Timestamp:HH:mm:ss.fff} {Level:u3}] {TraceId} {SourceContext}: {Message:lj}");

    public void Format(LogEvent logEvent, TextWriter output)
    {
        Line.Format(logEvent, output);
        foreach (var name in properties)
        {
            if (!logEvent.Properties.TryGetValue(name, out var value)) continue;

            output.Write(' ');
            output.Write(name);
            output.Write('=');
            if (value is ScalarValue { Value: string text }) output.Write(text);
            else value.Render(output);
        }
        output.WriteLine();
        if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception);
    }
}
