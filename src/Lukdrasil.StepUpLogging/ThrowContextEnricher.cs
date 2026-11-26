// Adapted from Serilog.ThrowContext (https://github.com/Tolyandre/serilog-throw-context-enricher).
// MIT License. Copyright (c) 2020 Anatoly Buranov.
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright
// notice and this permission notice shall be included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Adds to an event that carries an exception the log scopes that were open where that exception, or one of its inner
/// exceptions, was thrown. Every first-chance exception records the <c>LogContext</c> and the execution context; the
/// execution context restores the <c>AsyncLocal</c> scope of <c>Serilog.Extensions.Logging</c>, so <c>BeginScope</c>
/// scopes come back as well. A property the event already has is kept, and the deepest throw site wins among the
/// captures. The <c>FirstChanceException</c> subscription is process-wide and made once.
/// </summary>
internal sealed class ThrowContextEnricher : ILogEventEnricher
{
    private static readonly ConditionalWeakTable<Exception, List<(ILogEventEnricher LogContext, ExecutionContext ExecutionContext)>> Captured = new();
    private static readonly ConditionalWeakTable<LogEvent, object?> Enriching = new();

    static ThrowContextEnricher() => AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (ExecutionContext.Capture() is not { } executionContext)
        {
            return;
        }

        var captures = Captured.GetOrCreateValue(e.Exception);
        lock (captures)
        {
            captures.Add((LogContext.Clone(), executionContext));
        }
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Exception is null || !Enriching.TryAdd(logEvent, null))
        {
            return;
        }

        for (var exception = logEvent.Exception; exception is not null; exception = exception.InnerException)
        {
            if (!Captured.TryGetValue(exception, out var captures))
            {
                continue;
            }

            (ILogEventEnricher LogContext, ExecutionContext ExecutionContext)[] snapshot;
            lock (captures)
            {
                snapshot = [.. captures];
            }

            foreach (var (logContext, executionContext) in snapshot)
            {
                ExecutionContext.Run(executionContext, _ => logContext.Enrich(logEvent, propertyFactory), null);
            }
        }
    }
}
