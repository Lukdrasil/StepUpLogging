using System;
using System.Threading.Tasks;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class StepUpControllerTests
{
    [Fact]
    public async Task Trigger_SetsAndRestoresLevelAsync()
    {
        var opts = new StepUpLoggingOptions
        {
            BaseLevel = "Warning",
            StepUpLevel = "Information",
            DurationSeconds = 1
        };

        using var controller = new StepUpLoggingController(opts);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);

        // Wait a bit over the duration to allow the timer to restore the base level
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }
}

public class RedactionTests
{
    [Fact]
    public void Redact_ReplacesSensitiveValues()
    {
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex("password=[^&]*", System.Text.RegularExpressions.RegexOptions.Compiled),
            new System.Text.RegularExpressions.Regex("authorization:.*", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        };

        var compiled = new CompiledRedactionPatterns(patterns);

        var input = "user=alice&password=secret123&note=ok\nauthorization: Bearer abc.def";
        var redacted = compiled.Redact(input);

        Assert.DoesNotContain("secret123", redacted);
        Assert.DoesNotContain("Bearer abc.def", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_NoPatterns_NoChange()
    {
        var compiled = new CompiledRedactionPatterns(Array.Empty<System.Text.RegularExpressions.Regex>());
        var input = "nothing to redact";
        var redacted = compiled.Redact(input);
        Assert.Equal(input, redacted);
    }
}
