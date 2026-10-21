using System.Buffers;
using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The <see cref="ConsoleLogFormat.Text"/> console line in logfmt, every item as <c>Key=value</c>: <c>Timestamp=</c>,
/// <c>Level=</c>, the trace id as <c>TraceId=</c> or as a W3C <c>traceparent=</c> by <see cref="ConsoleTraceFormat"/>,
/// <c>SourceContext=</c> and <c>Message=</c> with the rendered message, then each listed property the event has as
/// <c>Name=value</c>, then <c>Exception=</c>. The trace id, <c>SourceContext</c> and the exception are left out when
/// absent. A value that is empty or holds a space, <c>=</c>, <c>"</c>, <c>\</c> or a control character is written in
/// double quotes with <c>"</c>, <c>\</c> and control characters escaped, so a logged value cannot forge a key or a
/// console line. With <c>shortKeys</c> the line keys are <c>t=</c>, <c>l=</c>, <c>tid=</c> or <c>tp=</c>, <c>src=</c>,
/// <c>msg=</c> and <c>ex=</c> (ADR 0034).
/// </summary>
internal sealed class ConsoleTextFormatter(string[] properties, ConsoleTraceFormat traceFormat, bool shortKeys = false) : ITextFormatter
{
    private sealed record Keys(MessageTemplateTextFormatter Header, string TraceId, string Traceparent, string SourceContext, string Message, string Exception);

    private static readonly Keys LongKeys = new(
        new("Timestamp={Timestamp:HH:mm:ss.fff} Level={Level:u3}"), " TraceId=", " traceparent=", " SourceContext=", " Message=", " Exception=");
    private static readonly Keys ShortKeys = new(
        new("t={Timestamp:HH:mm:ss.fff} l={Level:u3}"), " tid=", " tp=", " src=", " msg=", " ex=");
    private static readonly MessageTemplateTextFormatter Message = new("{Message:lj}");
    private static readonly SearchValues<char> NeedsQuotes =
        SearchValues.Create([.. " =\"\\\u007f", .. Enumerable.Range(0, ' ').Select(c => (char)c)]);

    private readonly Keys _keys = shortKeys ? ShortKeys : LongKeys;

    public void Format(LogEvent logEvent, TextWriter output)
    {
        _keys.Header.Format(logEvent, output);
        WriteTrace(logEvent, output);
        if (CategoryPrefix.TryGetSourceContext(logEvent, out var sourceContext))
        {
            output.Write(_keys.SourceContext);
            WriteValue(output, sourceContext);
        }

        using var message = new StringWriter();
        Message.Format(logEvent, message);
        output.Write(_keys.Message);
        WriteValue(output, message.ToString());
        foreach (var name in properties)
        {
            if (!logEvent.Properties.TryGetValue(name, out var value)) continue;

            output.Write(' ');
            output.Write(name);
            output.Write('=');
            WriteValue(output, value is ScalarValue { Value: string text } ? text : value.ToString());
        }
        if (logEvent.Exception is not null)
        {
            output.Write(_keys.Exception);
            WriteValue(output, logEvent.Exception.ToString());
        }
        output.WriteLine();
    }

    private static void WriteValue(TextWriter output, string value)
    {
        if (value.Length > 0 && value.AsSpan().IndexOfAny(NeedsQuotes) < 0)
        {
            output.Write(value);
            return;
        }

        output.Write('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Write("\\\""); break;
                case '\\': output.Write("\\\\"); break;
                case '\r': output.Write("\\r"); break;
                case '\n': output.Write("\\n"); break;
                case '\t': output.Write("\\t"); break;
                case < ' ' or '\u007f': output.Write($"\\u{(int)c:x4}"); break;
                default: output.Write(c); break;
            }
        }
        output.Write('"');
    }

    private void WriteTrace(LogEvent logEvent, TextWriter output)
    {
        if (logEvent.TraceId is not { } traceId) return;
        if (traceFormat == ConsoleTraceFormat.Traceparent && logEvent.SpanId is { } spanId)
        {
            output.Write($"{_keys.Traceparent}00-{traceId.ToHexString()}-{spanId.ToHexString()}-{Flags(logEvent)}");
            return;
        }
        output.Write($"{_keys.TraceId}{traceId.ToHexString()}");
    }

    /// <summary>The <c>TraceFlags</c> property as two hex digits, <c>00</c> when it is absent or not a byte.</summary>
    private static string Flags(LogEvent logEvent)
        => logEvent.Properties.TryGetValue("TraceFlags", out var value)
           && value is ScalarValue { Value: string text }
           && byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
            ? flags.ToString("x2", CultureInfo.InvariantCulture)
            : "00";

}
