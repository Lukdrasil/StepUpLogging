using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// One request summary, inbound or outbound (ADR 0032): the template properties every summary carries, then the
/// optional fields in the order they were added. The User-Agent is classified raw and redacted here, the one place
/// that rule is written (ADR 0032 D2).
/// </summary>
internal sealed class RequestSummary
{
    internal const string Inbound = "Inbound";
    internal const string Outbound = "Outbound";
    internal const string NoUserAgent = "(none)";

    private const int TemplateProperties = 7;
    // Above the most fields a summary carries, so adding a field never grows the list.
    private const int FieldCapacity = 16;

    private static readonly MessageTemplate Template =
        new MessageTemplateParser().Parse("{Direction} {Method} {Path} {StatusCode} {ElapsedMs} ms {ClientKind} {UserAgent}");

    private static readonly LogEventProperty IsRequestSummaryProperty =
        new(LogProperties.IsRequestSummary, new ScalarValue(true));

    private readonly string _direction;
    private readonly string _method;
    private readonly string _path;
    private readonly int? _statusCode;
    private readonly double _elapsedMs;
    private readonly string _clientKind;
    private readonly string _userAgent;
    private readonly List<KeyValuePair<string, object>> _fields = new(FieldCapacity);

    /// <summary>
    /// A summary whose <c>ClientKind</c> is classified from <paramref name="rawUserAgent"/> before it is redacted
    /// with <paramref name="patterns"/>; a missing or blank User-Agent is written as <see cref="NoUserAgent"/>.
    /// </summary>
    internal RequestSummary(string direction, string method, string path, int? statusCode, double elapsedMs, string? rawUserAgent, CompiledRedactionPatterns patterns)
    {
        _direction = direction;
        _method = method;
        _path = path;
        _statusCode = statusCode;
        _elapsedMs = elapsedMs;
        _clientKind = ClientKind.Classify(rawUserAgent);
        _userAgent = string.IsNullOrWhiteSpace(rawUserAgent) ? NoUserAgent : patterns.Redact(rawUserAgent);
    }

    /// <summary>Adds the field unless <paramref name="value"/> is null or empty.</summary>
    internal RequestSummary With(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _fields.Add(new(name, value));
        }

        return this;
    }

    /// <summary>Adds the field unless <paramref name="value"/> is null.</summary>
    internal RequestSummary With(string name, long? value)
    {
        if (value is { } present)
        {
            _fields.Add(new(name, present));
        }

        return this;
    }

    /// <summary>Adds the field whatever its value.</summary>
    internal RequestSummary With(string name, bool value)
    {
        _fields.Add(new(name, value));
        return this;
    }

    /// <summary>Adds the field only when <paramref name="value"/> is true.</summary>
    internal RequestSummary WithTrue(string name, bool value) => value ? With(name, value) : this;

    /// <summary>Adds the route parameters unless there are none.</summary>
    internal RequestSummary With(string name, IReadOnlyDictionary<string, object?>? value)
    {
        if (value is { Count: > 0 })
        {
            _fields.Add(new(name, value));
        }

        return this;
    }

    /// <summary>
    /// The summary as one event at <paramref name="level"/>, with the trace and span of <see cref="Activity.Current"/>;
    /// values are converted with <paramref name="logger"/>.
    /// </summary>
    internal LogEvent ToLogEvent(LogEventLevel level, Serilog.ILogger logger)
    {
        var summary = CreateEvent(level, logger);
        foreach (var (name, value) in _fields)
        {
            summary.AddPropertyIfAbsent(SummaryValueFactory.Instance, name, FieldValue(logger, value));
        }

        summary.AddPropertyIfAbsent(IsRequestSummaryProperty);
        return summary;
    }

    private LogEvent CreateEvent(LogEventLevel level, Serilog.ILogger logger)
    {
        var templateProperties = new List<LogEventProperty>(TemplateProperties);
        AddBound(templateProperties, logger, "Direction", _direction);
        AddBound(templateProperties, logger, "Method", _method);
        AddBound(templateProperties, logger, "Path", _path);
        if (_statusCode.HasValue)
        {
            AddBound(templateProperties, logger, "StatusCode", _statusCode.Value);
        }

        AddBound(templateProperties, logger, "ElapsedMs", _elapsedMs);
        AddBound(templateProperties, logger, "ClientKind", _clientKind);
        AddBound(templateProperties, logger, "UserAgent", _userAgent);
        var activity = Activity.Current;
        return activity is null
            ? new LogEvent(DateTimeOffset.Now, level, null, Template, templateProperties)
            : new LogEvent(DateTimeOffset.Now, level, null, Template, templateProperties, activity.TraceId, activity.SpanId);
    }

    private static void AddBound(List<LogEventProperty> properties, Serilog.ILogger logger, string name, object? value)
    {
        if (logger.BindProperty(name, value, false, out var property))
        {
            properties.Add(property);
        }
    }

    private static object FieldValue(Serilog.ILogger logger, object value)
        => value is IReadOnlyDictionary<string, object?> routeParameters ? RouteParameters(logger, routeParameters) : value;

    private static DictionaryValue RouteParameters(Serilog.ILogger logger, IReadOnlyDictionary<string, object?> routeParameters)
    {
        var elements = new KeyValuePair<ScalarValue, LogEventPropertyValue>[routeParameters.Count];
        var index = 0;
        foreach (var (key, value) in routeParameters)
        {
            elements[index++] = new(new ScalarValue(key), RouteValue(logger, value));
        }

        return new DictionaryValue(elements);
    }

    // A custom ILogger that does not override BindProperty returns false while IsEnabled is true, so the fallback is reachable.
    private static LogEventPropertyValue RouteValue(Serilog.ILogger logger, object? value) =>
        value is string or null || !logger.BindProperty("RouteValue", value, false, out var property)
            ? new ScalarValue(value)
            : property.Value;

    /// <summary>
    /// Adds property values to a <see cref="LogEvent"/> through <c>AddPropertyIfAbsent(factory, name, value)</c>,
    /// which stores the value without the <see cref="LogEventProperty"/> wrapper the one-argument overload needs.
    /// A string is a scalar under every Serilog conversion policy; a value already converted is stored as it is.
    /// </summary>
    private sealed class SummaryValueFactory : ILogEventPropertyFactory, ILogEventPropertyValueFactory
    {
        internal static readonly SummaryValueFactory Instance = new();

        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects) =>
            value as LogEventPropertyValue ?? new ScalarValue(value);

        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, CreatePropertyValue(value, destructureObjects));
    }
}
