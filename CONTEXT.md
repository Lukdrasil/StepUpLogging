# Context

Domain terms of `Lukdrasil.StepUpLogging`.

- **floor**: a per-prefix minimum level from `CategoryFloors`. It only raises the category's
  minimum to `max(switch, floor)` and never makes the category more verbose. Avoid: override,
  category level.
- **Diagnostic mode**: `Mode = Diagnostic`, entered only at startup. The switch sits at
  `DiagnosticLevel` for `DiagnosticDurationMinutes`, then the controller returns to a normal mode.
  Avoid: debug mode, verbose mode.
- **exempt category**: a prefix in `DiagnosticExemptCategories` that keeps its floor during
  Diagnostic. Avoid: pinned category.
- **held-back event**: (ADR 0023) an event `StepUpSink` did not export, handed to
  `PreErrorBufferSink.Hold`. Avoid: gated event.
- **scope RequestPath**: (#66) the `RequestPath` property ASP.NET Core's hosting log scope adds,
  which Serilog.Extensions.Logging pushes onto `LogContext` so every logger with `FromLogContext`
  attaches it. Avoid: hosting path, MEL path.
- **path-bearing property**: (#66) an event property that repeats the request path: `RequestPath`
  on any event, `Path` on `Microsoft.AspNetCore.Hosting.Diagnostics` events. Avoid: path field,
  URL property.
