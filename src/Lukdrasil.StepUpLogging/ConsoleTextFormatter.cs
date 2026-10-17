using System.Text;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The <see cref="ConsoleLogFormat.Text"/> console line: time, level, trace id, <c>SourceContext</c> and the rendered
/// message, then each listed property the event has as <c>Name=value</c>, then the exception on the following lines.
/// The trace id and <c>SourceContext</c> are left out when absent. Line breaks in the message and the property values
/// are written as <c>\r</c> and <c>\n</c>, so a logged value cannot forge a console line.
/// </summary>
internal sealed class ConsoleTextFormatter(string[] properties) : ITextFormatter
{
    private static readonly MessageTemplateTextFormatter Header = new("[{Timestamp:HH:mm:ss.fff} {Level:u3}]");
    private static readonly MessageTemplateTextFormatter Message = new("{Message:lj}");

    public void Format(LogEvent logEvent, TextWriter output)
    {
        Header.Format(logEvent, output);
        if (logEvent.TraceId is { } traceId)
        {
            output.Write(' ');
            output.Write(traceId.ToHexString());
        }
        if (CategoryPrefix.TryGetSourceContext(logEvent, out var sourceContext))
        {
            output.Write(' ');
            output.Write(sourceContext);
            output.Write(':');
        }
        output.Write(' ');

        var line = new LineBreakEscapingWriter(output);
        Message.Format(logEvent, line);
        foreach (var name in properties)
        {
            if (!logEvent.Properties.TryGetValue(name, out var value)) continue;

            output.Write(' ');
            output.Write(name);
            output.Write('=');
            if (value is ScalarValue { Value: string text }) line.Write(text);
            else value.Render(line);
        }
        output.WriteLine();
        if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception);
    }

    private sealed class LineBreakEscapingWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            switch (value)
            {
                case '\r': inner.Write("\\r"); break;
                case '\n': inner.Write("\\n"); break;
                default: inner.Write(value); break;
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            if (value.AsSpan().IndexOfAny('\r', '\n') < 0) inner.Write(value);
            else foreach (var c in value) Write(c);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            foreach (var c in buffer.AsSpan(index, count)) Write(c);
        }
    }
}
