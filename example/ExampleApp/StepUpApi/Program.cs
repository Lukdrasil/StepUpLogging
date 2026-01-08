using Scalar.AspNetCore;
using Lukdrasil.StepUpLogging;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Enable StepUp logging (Serilog + request logging)
// Logs are exported to OpenTelemetry OTLP endpoint (default: localhost:4317)
// Console logging can be enabled via configuration for dev scenarios
builder.AddStepUpLogging();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("StepUp API Reference"));
}

// StepUp request logging middleware (captures enriched request data when stepped up)
app.UseStepUpRequestLogging();
app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

// Demo endpoints using StepUp implementation
app.MapGet("/demo/logs", (ILoggerFactory lf) =>
{
    var logger = lf.CreateLogger("Demo");
    logger.LogTrace("Trace message from StepUpApi");
    logger.LogDebug("Debug message from StepUpApi");
    logger.LogInformation("Information message from StepUpApi");
    logger.LogWarning("Warning message from StepUpApi");
    logger.LogError("Error message from StepUpApi");
    return Results.Ok(new { message = "Emitted Trace..Error logs (StepUpApi)" });
})
.WithName("DemoLogs");

app.MapPost("/demo/echo", async (HttpContext http, ILoggerFactory lf) =>
{
    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync();
    var logger = lf.CreateLogger("Demo");
    logger.LogInformation("Echo called with body length {Length}", body?.Length ?? 0);
    return Results.Text(body ?? string.Empty, "application/json");
})
.WithName("DemoEcho");

app.MapPost("/stepup/trigger", (StepUpLoggingController controller) =>
{
    controller.Trigger();
    return Results.Ok(new { message = "StepUp triggered: elevated logging for a short time" });
})
.WithName("StepUpTrigger");

app.MapGet("/stepup/status", (StepUpLoggingController controller) =>
{
    return Results.Ok(new { active = controller.IsSteppedUp });
})
.WithName("StepUpStatus");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
