using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class AddStepUpLoggingRegistrationTests
{
    [Fact]
    public void AddStepUpLogging_RegistersRequiredServices()
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.AddStepUpLogging();

        using var app = builder.Build();
        var sp = app.Services;

        var controller = sp.GetService<StepUpLoggingController>();
        var patterns = sp.GetService<CompiledRedactionPatterns>();

        Assert.NotNull(controller);
        Assert.NotNull(patterns);
    }
}
