using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Wire Serilog for standard request logging (no step-up)
builder.Host.UseSerilog((ctx, services, lc) =>
{
    lc.ReadFrom.Configuration(ctx.Configuration)
      .Enrich.FromLogContext()
      .Enrich.WithProperty("Application", ctx.HostingEnvironment.ApplicationName)
      .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
      .WriteTo.Async(a => a.Console(new CompactJsonFormatter()));
}, writeToProviders: true);

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("Simple API Reference"));
}

// Standard Serilog request logging (no step-up)
app.UseSerilogRequestLogging();
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

// Demo endpoints to compare with StepUpApi
app.MapGet("/demo/logs", (ILoggerFactory lf) =>
{
    var logger = lf.CreateLogger("Demo");
    logger.LogTrace("Trace message from SimpleApi");
    logger.LogDebug("Debug message from SimpleApi");
    logger.LogInformation("Information message from SimpleApi");
    logger.LogWarning("Warning message from SimpleApi");
    logger.LogError("Error message from SimpleApi");
    return Results.Ok(new { message = "Emitted Trace..Error logs (SimpleApi)" });
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

app.MapPost("/stepup/trigger", () => Results.BadRequest(new { message = "StepUp logging is not enabled in SimpleApi" }))
   .WithName("StepUpTrigger");

app.MapGet("/stepup/status", () => Results.Ok(new { active = false }))
   .WithName("StepUpStatus");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
