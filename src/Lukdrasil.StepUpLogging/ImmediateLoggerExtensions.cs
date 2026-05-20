using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Extension methods on <see cref="ILogger"/> that mark a log event as <em>immediate</em>,
/// causing it to bypass the step-up <see cref="Serilog.Core.LoggingLevelSwitch"/> and export
/// directly regardless of the current base log level.
/// </summary>
/// <remarks>
/// The marker is applied via <see cref="ILogger.BeginScope{TState}"/> with a property named
/// <c>IsImmediate=true</c>. Serilog's MEL provider translates the scope dictionary into a
/// log-event property, which <see cref="ImmediateSink"/> detects and forwards to the bypass logger.
/// </remarks>
public static class ImmediateLoggerExtensions
{
    // Shared read-only dictionary — safe for concurrent use because it is never mutated after creation.
    private static readonly Dictionary<string, object> ImmediateScopeState =
        new() { [LogProperties.IsImmediate] = true };

    /// <summary>Emits a log event at <paramref name="level"/> that always exports, regardless of step-up state.</summary>
    public static void LogImmediate(this ILogger logger, LogLevel level, string? message, params object?[] args)
    {
        using var _ = logger.BeginScope(ImmediateScopeState);
        logger.Log(level, message, args);
    }

    /// <summary>Emits a log event at <paramref name="level"/> with an associated exception that always exports.</summary>
    public static void LogImmediate(this ILogger logger, LogLevel level, Exception? exception, string? message, params object?[] args)
    {
        using var _ = logger.BeginScope(ImmediateScopeState);
        logger.Log(level, exception, message, args);
    }

    /// <summary>Emits an <c>Information</c> event that always exports, regardless of step-up state.</summary>
    public static void LogImmediateInformation(this ILogger logger, string? message, params object?[] args)
    {
        using var _ = logger.BeginScope(ImmediateScopeState);
        logger.LogInformation(message, args);
    }

    /// <summary>Emits a <c>Warning</c> event that always exports, regardless of step-up state.</summary>
    public static void LogImmediateWarning(this ILogger logger, string? message, params object?[] args)
    {
        using var _ = logger.BeginScope(ImmediateScopeState);
        logger.LogWarning(message, args);
    }

    /// <summary>Emits an <c>Error</c> event that always exports, regardless of step-up state.</summary>
    public static void LogImmediateError(this ILogger logger, string? message, params object?[] args)
    {
        using var _ = logger.BeginScope(ImmediateScopeState);
        logger.LogError(message, args);
    }

    /// <summary>Emits an <c>Error</c> event with an associated exception that always exports.</summary>
    public static void LogImmediateError(this ILogger logger, Exception? exception, string? message, params object?[] args)
    {
        using var _ = logger.BeginScope(ImmediateScopeState);
        logger.LogError(exception, message, args);
    }

    /// <summary>
    /// Opens a scope in which all log events emitted via <paramref name="logger"/> are marked
    /// as immediate and will always export regardless of step-up state.
    /// </summary>
    /// <example>
    /// <code>
    /// using (logger.BeginImmediateScope())
    /// {
    ///     logger.LogInformation("Step 1 complete");
    ///     logger.LogInformation("Step 2 complete");
    /// }
    /// </code>
    /// </example>
    public static IDisposable? BeginImmediateScope(this ILogger logger)
        => logger.BeginScope(ImmediateScopeState);
}
