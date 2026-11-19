using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class DiagnosticContextRegistrationTests
{
    [Fact]
    public async Task RealPipeline_ActivatesRequestLogging_WithoutManualDiagnosticContext()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddStepUpLogging(configureOptions: opts => opts.EnableOtlpExporter = false);

        await using var app = builder.Build();

        app.UseStepUpRequestLogging();
        app.MapGet("/ping", () => "pong");

        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();

        // UseSerilogRequestLogging activates its RequestLoggingMiddleware lazily on
        // the first request; that middleware requires a DiagnosticContext from DI.
        // If DiagnosticContext were unregistered, the request would fault instead of
        // returning 200 — so a successful response proves the middleware activated.
        var response = await client.GetAsync("/ping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
