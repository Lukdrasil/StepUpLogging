# Migrating to v2.0.0

v2.0.0 is a **breaking** release. It bundles three breaking changes plus several
non-breaking fixes. This guide covers each break with before/after code.

## 1. `enableConsoleLogging` parameter removed

Console logging is now driven **solely** by `StepUpLoggingOptions.EnableConsoleLogging`.
The `enableConsoleLogging` parameter was removed from both `AddStepUpLogging(...)` overloads.

**Before (v1):**

```csharp
builder.AddStepUpLogging(
    configureOptions: opts => opts.CaptureRequestBody = true,
    enableConsoleLogging: true);
```

**After (v2):**

```csharp
builder.AddStepUpLogging(opts =>
{
    opts.CaptureRequestBody = true;
    opts.EnableConsoleLogging = true;
});
```

You can also set it from configuration: `"SerilogStepUp": { "EnableConsoleLogging": true }`.

## 2. `configure` delegate signature changed

The `configure` overload's delegate changed from
`Action<HostBuilderContext, IServiceProvider, LoggerConfiguration>` to
`Action<IServiceProvider, LoggerConfiguration>`.

The old first argument was a **fake** `HostBuilderContext` synthesized internally with a
null `Configuration` and null `HostingEnvironment` — touching either threw a
`NullReferenceException`. It has been removed. Resolve `IConfiguration` /
`IHostEnvironment` from the `IServiceProvider` instead.

**Before (v1):**

```csharp
builder.AddStepUpLogging((ctx, sp, lc) =>
{
    // ctx.Configuration / ctx.HostingEnvironment were null here (NRE risk)
    lc.WriteTo.Console();
});
```

**After (v2):**

```csharp
builder.AddStepUpLogging((sp, lc) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    lc.WriteTo.Console();
});
```

## 3. Config `Serilog:WriteTo` sinks now respect the step-up LevelSwitch

Previously a `Serilog:WriteTo` sink declared in `appsettings.json` attached to the
**Verbose root**, so it exported **everything at Verbose, permanently**, bypassing the
step-up `LevelSwitch` entirely — the opposite of the library's purpose.

In v2 those config-declared sinks are attached to the **step-up-gated inner logger**.
They now honor `BaseLevel` / `StepUpLevel` like the library's own sinks: below the current
switch level, events are dropped; once an error steps the level up, they flow through.

If you relied on a config `WriteTo` sink exporting **everything, always** from the root,
switch to the bypass routes, which export independently of the LevelSwitch:

- Mark events `IsImmediate = true` (or use the immediate-logging extension methods) to
  send them straight to the bypass sinks regardless of level.
- Mark events `IsRequestSummary = true` (e.g. via `AlwaysLogRequestSummary`) for
  always-on request summaries.

Otherwise, accept that those sinks now gate with step-up — which is the intended,
least-surprise behavior.
