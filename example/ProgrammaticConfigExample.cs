using Lukdrasil.StepUpLogging;

var builder = WebApplication.CreateBuilder(args);

// =============================================================================
// OPTION 1: Load configuration from appsettings.json (recommended for most cases)
// =============================================================================
// builder.AddStepUpLogging();

// =============================================================================
// OPTION 2: Programmatic configuration (useful for dynamic settings)
// =============================================================================
builder.AddStepUpLogging(opts =>
{
    // Step-up mode
    opts.Mode = builder.Environment.IsDevelopment() 
        ? StepUpMode.AlwaysOn  // Dev: see all logs
        : StepUpMode.Auto;     // Prod: step-up on errors

    // Log levels
    opts.BaseLevel = "Warning";
    opts.StepUpLevel = builder.Environment.IsDevelopment() ? "Debug" : "Information";
    opts.DurationSeconds = 180;

    // OpenTelemetry export (primary). Endpoint, protocol, headers and resource attributes come from
    // OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL, OTEL_EXPORTER_OTLP_HEADERS and OTEL_RESOURCE_ATTRIBUTES.
    opts.EnableOtlpExporter = true;
    opts.ServiceVersion = "1.0.0";

    // Console logging (for local dev or when OTLP collector is not available)
    opts.EnableConsoleLogging = builder.Environment.IsDevelopment();

    // Request body capture
    opts.CaptureRequestBody = true;
    opts.MaxBodyCaptureBytes = 8192;

    // Sensitive data redaction
    opts.RedactionRegexes = new[]
    {
        "password=[^&]*",
        "authorization:.*",
        "api[_-]?key=[^&]*"
    };

    // Paths to exclude from logging
    opts.ExcludePaths = new[] { "/health", "/healthz", "/metrics", "/ready" };
});

// =============================================================================
// OPTION 3: Mixed (appsettings.json base + programmatic overrides)
// =============================================================================
// builder.AddStepUpLogging(opts =>
// {
//     // appsettings.json is loaded first, then you can override specific values
//     if (builder.Environment.IsProduction())
//     {
//         opts.Mode = StepUpMode.Auto;
//         opts.EnableConsoleLogging = false;
//     }
// });

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();

// Enable step-up request logging middleware
app.UseStepUpRequestLogging();

app.MapGet("/demo/logs", (ILoggerFactory lf) =>
{
    var logger = lf.CreateLogger("Demo");
    logger.LogTrace("Trace level");
    logger.LogDebug("Debug level");
    logger.LogInformation("Information level");
    logger.LogWarning("Warning level");
    logger.LogError("Error level - this triggers step-up in Auto mode!");
    return Results.Ok(new { message = "Logs emitted" });
})
.WithName("DemoLogs");

// Manual step-up control endpoints
app.MapPost("/stepup/trigger", (StepUpLoggingController controller) =>
{
    controller.Trigger();
    return Results.Ok(new { message = "Step-up manually triggered", active = controller.IsSteppedUp });
})
.WithName("TriggerStepUp");

app.MapGet("/stepup/status", (StepUpLoggingController controller) =>
{
    return Results.Ok(new 
    { 
        active = controller.IsSteppedUp,
        currentLevel = controller.LevelSwitch.MinimumLevel.ToString()
    });
})
.WithName("StepUpStatus");

app.Run();
