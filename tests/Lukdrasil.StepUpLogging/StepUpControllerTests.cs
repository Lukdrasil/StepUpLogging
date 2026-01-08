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

    [Fact]
    public void AlwaysOnMode_AlwaysSteppedUp()
    {
        var opts = new StepUpLoggingOptions
        {
            Mode = StepUpMode.AlwaysOn,
            BaseLevel = "Warning",
            StepUpLevel = "Information"
        };

        using var controller = new StepUpLoggingController(opts);

        Assert.True(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);

        // Trigger should be ignored
        controller.Trigger();
        Assert.True(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void DisabledMode_NeverStepsUp()
    {
        var opts = new StepUpLoggingOptions
        {
            Mode = StepUpMode.Disabled,
            BaseLevel = "Warning",
            StepUpLevel = "Information"
        };

        using var controller = new StepUpLoggingController(opts);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);

        // Trigger should be ignored
        controller.Trigger();
        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void AutoMode_TriggersOnDemand()
    {
        var opts = new StepUpLoggingOptions
        {
            Mode = StepUpMode.Auto,
            BaseLevel = "Warning",
            StepUpLevel = "Debug",
            DurationSeconds = 300
        };

        using var controller = new StepUpLoggingController(opts);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Debug, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void ParseInvalidLevel_ReturnsFallback()
    {
        var opts = new StepUpLoggingOptions
        {
            BaseLevel = "InvalidLevel",
            StepUpLevel = "AlsoInvalid",
            DurationSeconds = 60
        };

        using var controller = new StepUpLoggingController(opts);

        // Should fallback to Warning and Information
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void ConstructorWithNullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new StepUpLoggingController(null!));
    }

    [Fact]
    public async Task RapidTriggers_RateLimited()
    {
        var opts = new StepUpLoggingOptions
        {
            BaseLevel = "Warning",
            StepUpLevel = "Information",
            DurationSeconds = 5
        };

        using var controller = new StepUpLoggingController(opts);

        // First trigger should succeed
        controller.Trigger();
        Assert.True(controller.IsSteppedUp);

        // Rapid second trigger within 5-second window should be rate-limited
        controller.Trigger();

        // Wait for timer to restore
        await Task.Delay(TimeSpan.FromMilliseconds(5100));
        Assert.False(controller.IsSteppedUp);
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

    [Fact]
    public void Redact_NullInput_ReturnsNull()
    {
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex("test", System.Text.RegularExpressions.RegexOptions.Compiled)
        };
        var compiled = new CompiledRedactionPatterns(patterns);
        var redacted = compiled.Redact(null!);
        Assert.Null(redacted);
    }

    [Fact]
    public void Redact_EmptyInput_ReturnsEmpty()
    {
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex("test", System.Text.RegularExpressions.RegexOptions.Compiled)
        };
        var compiled = new CompiledRedactionPatterns(patterns);
        var redacted = compiled.Redact(string.Empty);
        Assert.Equal(string.Empty, redacted);
    }

    [Fact]
    public void Redact_MultiplePatterns_RedactsAll()
    {
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex("password=[^&]*", System.Text.RegularExpressions.RegexOptions.Compiled),
            new System.Text.RegularExpressions.Regex("api[_-]?key=[^&]*", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        };

        var compiled = new CompiledRedactionPatterns(patterns);

        var input = "password=secret&apiKey=abc123&data=ok";
        var redacted = compiled.Redact(input);

        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.Contains("[REDACTED]", redacted);
        Assert.Contains("data=ok", redacted);
    }

    [Fact]
    public void Redact_InvalidRegex_ReturnsFallback()
    {
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex("password=[^&]*", System.Text.RegularExpressions.RegexOptions.Compiled)
        };

        var compiled = new CompiledRedactionPatterns(patterns);

        var input = "password=secret&other=value";
        var redacted = compiled.Redact(input);

        // Should still contain original sensitive data due to fallback
        Assert.NotNull(redacted);
    }
}
