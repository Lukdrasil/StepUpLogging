# Context

Domain terms of `Lukdrasil.StepUpLogging`.

- **floor**: a per-prefix minimum level from `CategoryFloors`. It only raises the category's
  minimum to `max(switch, floor)` and never makes the category more verbose. Avoid: override,
  category level.
- **framework default**: (ADR 0029) a built-in floor rule for a framework category, applied by
  `FrameworkCategoryDefaults` only when no `CategoryFloors` key matches; it may also keep the category
  out of the pre-error buffer. Avoid: default floor, system level.
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
- **native image**: (ADR 0035) a consumer app published with `PublishAot=true`, where
  `RuntimeFeature.IsDynamicCodeSupported` is false and reflection-based JSON is off. Avoid: AOT build, trimmed app.
- **stuck record**: (ADR 0037) a spooled audit record with at least one failed delivery attempt in this
  process, still in the spool. Avoid: failed record, pending record.
- **record trace**: (ADR 0037) the trace a stuck record's retry warnings and its closing entry share.
  Avoid: delivery trace, spool trace.
- **client prefix**: (ADR 0042) the network, /24 for IPv4 and /48 for IPv6, written in CIDR form in place of the
  client address when `ClientIpLogging` or `ClientIpAudit` is `Prefix`. Avoid: anonymized IP, truncated IP,
  masked IP.
- **client error request**: an inbound request answered with a status from 400 to 499. Its caller's identity is
  logged as `ClientIpLoggingOnClientError` says, never less than `ClientIpLogging`. A 4xx is a suspicion, not proof
  of an attack. Avoid: attack, suspicious request, failed request (ADR 0043: >= 400 or an exception).
