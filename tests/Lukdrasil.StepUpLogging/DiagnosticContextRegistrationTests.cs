using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class DiagnosticContextRegistrationTests
{
    [Fact]
    public async Task RealPipeline_ActivatesRequestLogging_WithoutManualDiagnosticContext()
    {
        var previous = Log.Logger;
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.AddStepUpLogging(configureOptions: opts => opts.EnableOtlpExporter = false);

            await using var app = builder.Build();

            // Serilog.AspNetCore self-registers the DiagnosticContext. The interface
            // resolves even though this test never registers a DiagnosticContext of its
            // own — and the library's reflective registration only ever supplied the
            // concrete DiagnosticContext, never IDiagnosticContext. So the interface
            // resolving here can only have come from the framework.
            Assert.NotNull(app.Services.GetService<IDiagnosticContext>());

            app.UseStepUpRequestLogging();
            app.MapGet("/ping", () => "pong");

            await app.StartAsync();

            using var client = app.GetTestClient();

            // UseSerilogRequestLogging activates its RequestLoggingMiddleware lazily on
            // the first request; that middleware requires a DiagnosticContext from DI.
            // If DiagnosticContext were unregistered, the request would fault instead of
            // returning 200 — so a successful response proves the middleware activated.
            var response = await client.GetAsync("/ping");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await app.StopAsync();
        }
        finally
        {
            Log.Logger = previous;
        }
    }
}
