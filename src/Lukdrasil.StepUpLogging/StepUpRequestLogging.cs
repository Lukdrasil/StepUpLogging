using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Inbound request logging: the request summary middleware, the early body buffering (ADR 0004), and the Serilog
/// request completion event with its redacted path, query, route values, headers, client address, token id and,
/// while stepped up or on a 5xx, the request body (ADR 0009, ADR 0011).
/// </summary>
internal sealed class StepUpRequestLogging(StepUpSettings settings, StepUpLoggingController controller, CompiledRedactionPatterns patterns)
{
    // Read a margin beyond the limit so a secret straddling the cut still matches its pattern (ADR 0009 F2).
    private const int RedactionMargin = 256;

    /// <summary>The <c>SourceContext</c> of the request completion event, which Serilog.AspNetCore writes.</summary>
    internal const string CompletionSourceContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private static readonly LogEventProperty CompletionSource = new("SourceContext", new ScalarValue(CompletionSourceContext));

    // HttpContext.Items keys: the failure noted for Enrich, and the mark that the completion event carries the summary.
    private static readonly object NotedFailure = new();
    private static readonly object CompletionCarriesSummary = new();

    // The summary fields the completion event sets itself, after the carried ones.
    private static readonly HashSet<string> SetByCompletion = ["ClientIp", LogProperties.Jti, "QueryString", "RouteParameters"];

    private static readonly Meter Meter = new("StepUpLogging.RequestLogging", "1.0.0");
    private static readonly Counter<long> BodyCaptureCounter = Meter.CreateCounter<long>("request_body_captured_total", "count", "Number of requests with captured body");
    private static readonly Counter<long> RedactionCounter = Meter.CreateCounter<long>("request_redaction_applied_total", "count", "Number of requests with redaction applied");

    private readonly RequestSummaryWriter _summaries = new(settings, patterns, controller);

    /// <summary>Adds the request logging middleware to <paramref name="app"/>; completion events go to <paramref name="logger"/>.</summary>
    internal void UseIn(IApplicationBuilder app, ILogger? logger)
    {
        if (settings.AlwaysLogRequestSummary)
        {
            app.Use(SummarizeAsync);
        }

        if (settings.CaptureRequestBody)
        {
            app.Use(BufferBody);
        }

        app.UseSerilogRequestLogging(options =>
        {
            options.Logger = logger;
            options.EnrichDiagnosticContext = Enrich;
            options.GetLevel = OmitsSummaryOnError ? LevelNotingFailedRequest : LevelOf;
            options.GetMessageTemplateProperties = (context, _, elapsedMs, statusCode) =>
            [
                new LogEventProperty("RequestMethod", new ScalarValue(context.Request.Method)),
                new LogEventProperty("RequestPath", new ScalarValue(patterns.Redact(context.Request.Path.Value ?? string.Empty))),
                new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
                new LogEventProperty("Elapsed", new ScalarValue(elapsedMs)),
            ];
        });
    }

    private bool OmitsSummaryOnError => settings.AlwaysLogRequestSummary && settings.RequestSummaryOnError == RequestSummaryOnErrorMode.Omit;

    /// <summary>
    /// Runs the request and writes its summary afterwards, unless its completion event carries it (ADR 0043). An
    /// unhandled exception must not swallow the summary, since failing requests are the ones worth summarizing, so it
    /// is written with 500 and the exception rethrown.
    /// </summary>
    internal async Task SummarizeAsync(HttpContext context, Func<Task> next)
    {
        var start = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next();
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (!context.Items.ContainsKey(CompletionCarriesSummary))
            {
                _summaries.WriteInbound(context, StatusOf(context, failed), Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
    }

    /// <summary>
    /// Enables buffering before the endpoint reads the body, so <see cref="Enrich"/> can rewind and re-read it at
    /// completion. Doing it at completion was too late: the body was already consumed from a non-buffered stream.
    /// </summary>
    private static Task BufferBody(HttpContext context, Func<Task> next)
    {
        if (HasBody(context.Request))
        {
            context.Request.EnableBuffering();
        }

        return next();
    }

    private static bool HasBody(HttpRequest request) => request.Method is "POST" or "PUT" or "PATCH";

    /// <summary>
    /// The level of the request completion event: Verbose for an excluded path, Error for an exception, else by
    /// status. A client that drops the connection is not an application failure: logging it at Error would let any
    /// caller flush the pre-error buffer and step the instance up just by disconnecting, so it is Information.
    /// </summary>
    internal LogEventLevel LevelOf(HttpContext context, double elapsedMs, Exception? exception)
    {
        if (settings.ExcludedPaths.Contains(ExcludedPaths.NormalizedPath(context.Request)))
        {
            return LogEventLevel.Verbose;
        }

        if (context.RequestAborted.IsCancellationRequested && exception is null or OperationCanceledException)
        {
            return LogEventLevel.Information;
        }

        return exception is null ? StatusLevel(context.Response.StatusCode) : LogEventLevel.Error;
    }

    /// <summary>
    /// <see cref="LevelOf"/>, noting the level and status of a failed request (status 400 or above, or an exception)
    /// for <see cref="Enrich"/>: an exception's 500 is not yet on the response when the completion event is written.
    /// </summary>
    private LogEventLevel LevelNotingFailedRequest(HttpContext context, double elapsedMs, Exception? exception)
    {
        var level = LevelOf(context, elapsedMs, exception);
        var status = StatusOf(context, exception is not null);
        if (status >= StatusCodes.Status400BadRequest)
        {
            context.Items[NotedFailure] = new FailedRequest(level, status);
        }

        return level;
    }

    // An unhandled exception is answered with 500, which is not on the response while it propagates.
    private static int StatusOf(HttpContext context, bool threw) => threw ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;

    // A 5xx with no exception is often a backend failure relayed by a proxy, not this application's;
    // TreatServerErrorStatusAsError = false keeps those out of the trigger path.
    private LogEventLevel StatusLevel(int status) => status switch
    {
        >= 500 when settings.TreatServerErrorStatusAsError => LogEventLevel.Error,
        >= 400 => LogEventLevel.Warning,
        _ => LogEventLevel.Information,
    };

    internal void Enrich(IDiagnosticContext diagnosticContext, HttpContext context)
    {
        using var activity = StartLogRequestActivity(context);
        var request = context.Request;
        var redactions = new RedactionNotes(settings.ActivityInstrumentation);
        CarrySummaryIfFailed(diagnosticContext, context);

        var path = ExcludedPaths.NormalizedPath(request);
        var redactedPath = redactions.Redact(patterns, path, "path");
        diagnosticContext.Set("RequestPath", redactedPath);
        diagnosticContext.Set("QueryString", redactions.Redact(patterns, request.QueryString.Value ?? string.Empty, "query_string"));
        if (request.RouteValues.Count > 0)
        {
            diagnosticContext.Set("RouteParameters", RequestPathRedaction.RedactRouteValues(request.RouteValues, path, redactedPath, patterns, redactions.Note));
        }

        diagnosticContext.Set("Headers", RequestHeaderRedaction.RedactRequestHeaders(request.Headers, settings.SensitiveHeaders, patterns, redactions.Note));
        SetIfPresent(diagnosticContext, "ClientIp", RequestClient.Addresses(context, settings.TrustForwardedHeaders, settings.ClientIpLogging, patterns).ClientIp);
        if (redactions.Close())
        {
            activity?.SetTag("security.redaction_applied", true);
        }

        var jti = context.User.FindFirst("jti")?.Value;
        SetIfPresent(diagnosticContext, LogProperties.Jti, string.IsNullOrEmpty(jti) ? null : patterns.RedactProperty(LogProperties.Jti, jti));

        // The step-up trigger sink is asynchronous, so the request whose Error causes the step-up is not stepped up
        // yet when this runs. A 5xx status is knowable here, so the failing request is captured too.
        if (ShouldCaptureBody(context))
        {
            diagnosticContext.Set("RequestBody", CaptureBody(context));
        }
    }

    /// <summary>
    /// When the request failed and its completion event will be exported, sets the summary's fields on it, routes it
    /// past the level switch so a step-down before it reaches the output logger cannot drop it, and marks the request
    /// so <see cref="SummarizeAsync"/> writes no summary. A failure here leaves the summary to be written.
    /// </summary>
    private void CarrySummaryIfFailed(IDiagnosticContext diagnosticContext, HttpContext context)
    {
        if (!context.Items.TryGetValue(NotedFailure, out var noted) || noted is not FailedRequest failure || !CompletionExports(failure.Level))
        {
            return;
        }

        try
        {
            if (_summaries.BuildInbound(context, failure.StatusCode, 0) is not { } summary)
            {
                return;
            }

            foreach (var (name, value) in summary.CarriedFields(SetByCompletion))
            {
                diagnosticContext.Set(name, value);
            }

            diagnosticContext.Set(LogProperties.IsImmediate, true);
            context.Items[CompletionCarriesSummary] = true;
        }
        catch
        {
            // Never let the summary fail the request; it is written as under Keep.
        }
    }

    /// <summary>Whether the output loggers export a completion event at <paramref name="level"/> now.</summary>
    private bool CompletionExports(LogEventLevel level)
        => settings.Categories.Exports(new LogEvent(DateTimeOffset.Now, level, null, MessageTemplate.Empty, [CompletionSource]), controller.LevelSwitch.MinimumLevel, () => controller.IsDiagnosticActive);

    private readonly record struct FailedRequest(LogEventLevel Level, int StatusCode);

    private static void SetIfPresent(IDiagnosticContext diagnosticContext, string name, string? value)
    {
        if (value is not null)
        {
            diagnosticContext.Set(name, value);
        }
    }

    private Activity? StartLogRequestActivity(HttpContext context)
    {
        var activity = StartActivity("LogRequest", ActivityKind.Server);
        if (activity is not null)
        {
            activity.SetTag("http.method", context.Request.Method);
            activity.SetTag("http.target", patterns.Redact(context.Request.Path.Value ?? string.Empty));
            activity.SetTag("http.scheme", context.Request.Scheme);
            activity.SetTag("http.host", context.Request.Host.Value);
        }

        return activity;
    }

    private Activity? StartActivity(string name, ActivityKind kind)
        => settings.ActivityInstrumentation ? StepUpLoggingActivitySources.RequestLogging.StartActivity(name, kind) : null;

    // Body.CanSeek is true only when the early buffering middleware ran for this request.
    private bool ShouldCaptureBody(HttpContext context)
        => settings.CaptureRequestBody
           && (controller.IsSteppedUp || context.Response.StatusCode >= 500)
           && HasBody(context.Request)
           && context.Request.Body.CanSeek;

    private string CaptureBody(HttpContext context)
    {
        try
        {
            using (StartActivity("CaptureRequestBody", ActivityKind.Internal))
            {
                return ReadRedactedBody(context);
            }
        }
        catch
        {
            return "[UNAVAILABLE]";
        }
    }

    /// <summary>
    /// The buffered body, redacted and then cut to <c>MaxBodyCaptureBytes</c>. The enricher is synchronous and
    /// Kestrel forbids synchronous reads by default, so this request is opted in; the stream is rewindable here.
    /// </summary>
    private string ReadRedactedBody(HttpContext context)
    {
        var bodyControl = context.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        var body = context.Request.Body;
        var limit = settings.MaxBodyCaptureBytes;
        var buffer = new char[limit + RedactionMargin];
        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8, true, 1024, leaveOpen: true);
        // ReadBlock loops until the buffer is full or the stream ends; a single Read can return short.
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        body.Position = 0;
        if (read == 0)
        {
            return "[EMPTY]";
        }

        var redacted = patterns.Redact(new string(buffer, 0, read));
        BodyCaptureCounter.Add(1);
        return redacted.Length > limit ? redacted[..limit] : redacted;
    }

    /// <summary>
    /// Counts the redactions of one request and, with activity instrumentation on, records them on one aggregate
    /// ApplyRedaction span (ADR 0009) started on the first redaction, so a request with nothing to redact has none.
    /// </summary>
    private sealed class RedactionNotes(bool instrumented)
    {
        private readonly List<string> _targets = [];
        private Activity? _activity;

        internal string Redact(CompiledRedactionPatterns patterns, string value, string target)
        {
            var redacted = patterns.Redact(value);
            if (!string.Equals(redacted, value, StringComparison.Ordinal))
            {
                Note(target);
            }

            return redacted;
        }

        internal void Note(string target)
        {
            RedactionCounter.Add(1);
            _targets.Add(target);
            if (instrumented)
            {
                _activity ??= StepUpLoggingActivitySources.RequestLogging.StartActivity("ApplyRedaction", ActivityKind.Internal);
            }
        }

        /// <summary>Ends the span; <see langword="true"/> when there was one.</summary>
        internal bool Close()
        {
            if (_activity is null)
            {
                return false;
            }

            _activity.SetTag("security.redaction_applied", true);
            _activity.SetTag("security.redaction_count", _targets.Count);
            _activity.SetTag("security.redaction_targets", string.Join(",", _targets));
            _activity.Dispose();
            return true;
        }
    }
}
