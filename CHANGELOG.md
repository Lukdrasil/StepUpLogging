# Changelog

All notable changes to this project will be documented in this file.

Releases from 1.8.0 onward are recorded here. Entries backfilled from each release tag's
`PackageReleaseNotes` and, where those were not updated per release, from the commits between tags.
Tags before 1.8.0 predate this file.

## [Unreleased]

## [5.2.0] - 2026-10-08

### Added
- The `audit_spool_oldest_age_seconds` gauge: age in whole seconds of the oldest record the drain worker is waiting on, 0 for an empty spool. It grows while delivery is stuck behind one record, however empty the spool still is. Fixes #71.
- `EncryptedSpoolOptions.OldestRecordMaxAge` (default none, must be greater than zero when set) and `EncryptedSpoolOptions.OldestRecordStatus` (default `Degraded`): the health check reports the status once the oldest spooled record is older than the maximum. Off unless the maximum is set. Fixes #71.
- `audit_spool_dead_lettered_total` is tagged `reason`: `rejected`, `corrupt` or `unreadable`. Fixes #71.

### Changed
- The delivery contract in the README and ADR 0020 now states how a receiver answers a record that names an unknown key: `503` while keys are not loaded yet (retried), `422` when the key is gone for good (dead-lettered). The status code is the whole contract. Fixes #71.

## [5.1.0] - 2026-10-07

### Added
- `EncryptedSpoolOptions.DeliveryBatchSize` (default 1, 1 to 1024). Above 1 the drain worker posts a run of records as one JSON array to `{EndpointBaseUrl}/audit/batch`, so one receiver round trip delivers many records instead of one; the receiver must implement that endpoint, store a batch whole or reject it whole, and deduplicate on `eventId`. A rejected batch is redelivered record by record to `/audit`, so only the record rejected on its own is dead-lettered. At the default the wire is unchanged. Part of #69. See docs/adr/0020-spool-durability-and-delivery-failures.md.
- `EncryptedSpoolOptions.SpoolFullRecheckInterval` (default 1 s, must be greater than zero): how often a spool found full is measured against the disk again, and the window of the dropped-record log. Part of #69.

### Changed
- Concurrent durable writes no longer queue behind one process-wide gate: a write reserves room in the spool's tally before it writes, so fsyncs overlap and the cap still holds. Part of #69.
- A full spool no longer scans the spool directory on every dropped write: it refuses from the tally and measures the disk again at most once per `SpoolFullRecheckInterval`. Dropped records are logged at Critical at a bounded rate: the first drop of each window by `EventId`, the rest as one summary (count, first and last `EventId`). Every drop is still counted on `audit_spool_rejected_full_total`. Part of #69.
- The drain worker posts the stored bytes unchanged, with a `Content-Length`, instead of re-serializing the envelope and sending it chunked. The body, media type and URI the receiver sees are unchanged. Part of #69.

## [5.0.2] - 2026-09-30

### Fixed
- The #30 fix missed the scope `RequestPath`: ASP.NET Core's hosting log scope attaches the raw request path as `RequestPath` to every event logged during a request, so a secret in the path still leaked through the request summary, the pre-error buffer flush and every application event, and through the `Path` of `Microsoft.AspNetCore.Hosting.Diagnostics` events. `RedactionRegexes` now applies to both on every exported event, whatever `RedactLogEventProperties` is set to. Fixes #66.

## [5.0.1] - 2026-09-30

### Fixed
- The package passed the build assets of `Microsoft.Extensions.Telemetry.Abstractions`, a dependency of `Microsoft.Extensions.Http.Resilience`, to consumers. Its `buildTransitive` props set `DisableMicrosoftExtensionsLoggingSourceGenerator=true`, which replaced the standard `[LoggerMessage]` source generator with `Microsoft.Gen.Logging` in every consuming project. The `Microsoft.Extensions.Http.Resilience` reference is now `PrivateAssets="build;buildtransitive;analyzers;contentfiles"`, and a shipped `buildTransitive` targets file removes every `Microsoft.Extensions.Telemetry.Abstractions` analyzer, `Microsoft.Gen.Logging` and `Microsoft.Gen.Metrics` included, so a consumer uses the standard generator again. A consumer that uses those generators should reference `Microsoft.Extensions.Telemetry.Abstractions` directly to keep them. Fixes #62.

## [5.0.0] - 2026-09-30

BREAKING. See MIGRATION.md for migration steps.

### Changed (breaking)
- `AddStepUpLogging` no longer assigns Serilog's static `Log.Logger`, and disposing the host no longer calls `Log.CloseAndFlush()`. Every host logs through its own DI logger, so the last host built no longer owns `Log.*` calls and disposing one host no longer silences another. Set the new `SetStaticLogger` option (default `false`) to restore the old behavior. `UseStepUpRequestLogging` now writes request completion events to the DI `Serilog.ILogger`. Fixes #31. The "Logging step up..." and "Logging step down..." warnings are now written through the controller's bypass logger instead of the static `Log.Warning`, in both modes, so they no longer pass through the root pipeline (configure-hook sinks, root `Serilog:Filter`/`Enrich`/`Properties`) and the step-down warning now exports even when `BaseLevel` is above Warning. See docs/adr/0025-static-logger-preserved-by-default.md.
- A configured `ExcludePaths` now replaces the built-in defaults (`/healthz`, `/metrics`, `/health`) instead of being appended to them. List the defaults in your configuration to keep them. An explicit `[]` excludes nothing. The defaults apply only when `ExcludePaths` is not configured. Fixes #32.

### Added
- `CategoryFloors` (default `{}`) sets a per-category minimum level: a `SourceContext` prefix exports at `max(switch, floor)`, the most specific matching prefix decides, and an event a floor rejects is held back for the pre-error buffer. `DiagnosticExemptCategories` (default `[]`) names the prefixes that keep their floor during Diagnostic mode. See docs/adr/0024-category-floors-and-diagnostic-mode.md.
- `AlwaysExportCategories` (default empty). Events whose `SourceContext` matches a listed prefix are exported once at any level through the immediate path, so for example `Microsoft.Hosting.Lifetime` startup messages reach the export while `BaseLevel` is Warning. A later error in the same trace does not export them again. Fixes #35. See docs/adr/0024-category-floors-and-diagnostic-mode.md.
- `NeverTriggerCategories` (default `[]`): `SourceContext` prefixes whose `Error`/`Fatal` events do not trigger step-up. The error is still exported and still flushes its trace's pre-error buffer. `sink_error_events_total` now counts only errors that requested a step-up trigger, so errors from these categories are not counted. Fixes #35.
- `Mode = Diagnostic`, entered at startup: the switch sits at `DiagnosticLevel` (default `"Debug"`) for `DiagnosticDurationMinutes` (default 30, 1 to 120), then the controller runs as `Auto`. `Trigger()` is a no-op during the window, start and end Warnings go through the bypass logger, and the new `stepup_diagnostic_active` metric reports the state. Diagnostic lifts the `NeverStepUpCategories` pin: with the default list, EF `Database.Command` SQL (and its parameters when `EnableSensitiveDataLogging` is on) is exported for the whole window, up to 120 minutes. To keep it quiet, add a `CategoryFloors` entry for the category and a `DiagnosticExemptCategories` entry for it. The window is timed by the `TimeProvider` from DI. Fixes #35. See docs/adr/0024-category-floors-and-diagnostic-mode.md.
- `AddStepUpLogging(configureOptions, configure, configSectionName, logFilePath)` overload that takes both the options callback and the `(IServiceProvider, LoggerConfiguration)` callback. The two existing overloads are unchanged. Fixes #35.
- `RedactLogEventProperties` (default `false`). When set, `RedactionRegexes` is also applied to the string-valued and URI-like (`PathString`, `QueryString`, `HostString`, `Uri`, the latter through its decoded `ToString()` form; #34) scalar properties of application log events — not just request metadata — so a secret passed as a message-template argument (e.g. `logger.LogInformation("token={T}", secret)`) is masked too. Opt-in: nothing changes for a consumer who does not set it. Interpolated templates, exception messages, and structured/sequence/dictionary values remain out of scope either way. Fixes #20. See docs/adr/0022-application-log-redaction-enricher.md.

### Fixed
- `RedactionRegexes` did not reach the request path, so a secret carried in the path leaked through the request summary `Path`, the "HTTP ..." event's `RequestPath`, the `LogRequest` span's `http.target` and the route values captured from it. All three now carry the redacted path, and a route value whose text sits inside a redacted part of the path is exported as `[REDACTED]`. `ExcludePaths` still matches the raw path. Fixes #30.
- Pre-error buffering exported an event twice: once at the current level and again when an error in the same trace flushed the buffer, or at shutdown. The buffer now holds only events the step-up gate did not export, so every event is exported at most once. Held-back events are dropped at shutdown instead of exported. Fixes #29. See docs/adr/0023-prebuffer-exactly-once-and-drop-on-dispose.md.
- The encrypted spool's delivery client no longer inherits resilience handlers from `ConfigureHttpClientDefaults`. A consumer's `AddStandardResilienceHandler()` used to retry the audit POST, override `DeliveryTimeout`, and log at Error; `DrainWorker` is now the only retrying layer. A handler you add to the delivery client after `AddEncryptedSpoolAuditSink` still applies. Adds a dependency on `Microsoft.Extensions.Http.Resilience` 10.0.0. Fixes #33. See docs/adr/0020-spool-durability-and-delivery-failures.md.

## [4.0.0] - 2026-08-13

BREAKING. See MIGRATION.md for rationale and migration steps.

### Changed (breaking)
- `AuditEvent.ActorType` is now `required` — it no longer defaults to `"user"`, so every object initializer must set it.
- `Success`/`Failure`/`Denied` take the actor kind as a third positional parameter (`action, actorId, actorType`). The old two-argument factories are gone.
- `AddAuditLogging<TSink>()` now throws when an `IAuditEventSink` is already registered ahead of it (either sink type first), naming the sink being added and, where known, the one already registered.
- `IAuditEventSink.WriteAsync` now returns `ValueTask<AuditWriteResult>` (`Stored` or `Dropped`, no zero member) instead of a bare `ValueTask`. A `Dropped` write is counted on the new `audit_events_dropped_total` counter, skips the companion log, and leaves `audit_events_total` unchanged; an unrecognized result (including `default`, which still compiles) throws `InvalidOperationException` naming the sink.

### Added
- `AuditEvent.OldValues`/`NewValues` (`IReadOnlyDictionary<string, object?>?`, caller-supplied, unredacted — same category as `Data`).
- `AuditEvent.EventId`, owned by the library: a UUIDv7 stamped on every `AuditAsync` call before the record reaches your sink, overwriting any value set through a `with` expression. A retrying or spooling sink can deliver a record more than once; `EventId` is what lets the receiver deduplicate. No source change required, but every record now carries an identifier field your sink did not see before.
- `audit_events_dropped_total` counter under the `StepUpLogging.Audit` meter, joining the existing `audit_events_total`/`audit_write_failures_total`.
- `EncryptedSpoolAuditSink`, registered via `AddEncryptedSpoolAuditSink` — spools audit records to disk write-ahead and drains them to a configured endpoint, encrypting each payload through the `IAuditPayloadEncryptor` port the host implements and supplies. Purely additive: nothing changes for a consumer who does not call it. Fixes #22.

## [3.5.0] - 2026-08-05

Audit logging: the records that answer "who did what, to what, and with what outcome" get a path step-up gating can never drop. Nothing changes for consumers who do not call `AddAuditLogging`. No public API break.

### Added
- `AddAuditLogging<TSink>()`, `IAuditLogger<T>` (`AuditAsync(AuditEvent)`, plus an overload taking a companion log written only after the audit write succeeds), the `AuditEvent` record, and `IAuditEventSink`. Audit writes bypass Serilog entirely and go straight to the sink, so the write is awaited and a sink failure propagates to the business call site instead of vanishing into Serilog's void return (ADR 0016).
- Counters under the `StepUpLogging.Audit` meter: `audit_events_total{outcome}` and `audit_write_failures_total`. Zero events over an observation window is itself the alarm that audit stopped working.

### Notes
- The package ships no `IAuditEventSink` implementation, by design: a default that mirrors to logs or silently swallows records manufactures false confidence in a trail nobody checked. The README carries a database-backed sink to copy. Audit is turned off by not calling `AddAuditLogging`; there is no configuration flag.
- `AddAuditLogging` requires `AddStepUpLogging` — audit resolves the client IP by the same rule as request logging (ADR 0008) rather than adding a second, weaker one. A host missing it refuses to start with a message naming both methods; the two calls are valid in either order. Fixes #19.

## [3.4.0] - 2026-07-27

Request logging no longer reports Error for failures that are not the application's. No public API break.

### Added
- `TreatServerErrorStatusAsError` (default `true`, today's behaviour). Set it to `false` in a reverse proxy or BFF: a request completing with a status code >= 500 but no exception is then logged at Warning instead of Error, so a backend-relayed 5xx no longer triggers step-up or flushes the pre-error buffer. An unhandled exception is still Error either way.

### Fixed
- A client-aborted request (closed tab, reload, dropped connection) was logged at Error, which triggered step-up and flushed the pre-error buffer. On an anonymous route that made log amplification externally controllable — any caller could dump the buffer and open a Debug window on the instance just by disconnecting. Such a request is now logged at Information: still visible at a normal BaseLevel, but no longer a trigger. A genuine exception on an also-aborted request is still Error. Behaviour change without an opt-out (it is the fix); note that a server-initiated cancellation, e.g. an in-flight request at shutdown, is likewise no longer Error. Fixes #17.

## [3.3.0] - 2026-07-23

Fixes from a source review plus one new export behaviour. No public API break.

### Added
- Bypass-routed events — immediate logs (LogImmediate*), request summaries, and pre-error buffer flushes — now also export to config-declared Serilog:WriteTo sinks, not only the built-in OTLP/console/file sinks. Previously, with EnableOtlpExporter false and no console or file sink, these events went nowhere. Behaviour note: a config-declared File sink now attaches to both the gated and bypass loggers, so it must set `shared: true` or it will hit a file lock (ADR 0003). The gated logger still drops immediate/summary events, so there is no double-emit.
- Startup warning (Auto mode) when StepUpLevel is not more verbose than BaseLevel — a likely misconfiguration. Warn, not fail: equal/inverted levels are still accepted and AlwaysOn/Disabled are unaffected (ADR 0007).

### Fixed
- OTLP protocol: the spec-correct OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf was ignored and silently fell back to gRPC. Both `http` and `http/protobuf` now select HttpProtobuf.
- Exclude paths: a wildcard entry like `/api/*` over-matched sibling paths such as `/apifoo`. Matching is now `path == prefix` or `path` starts with `prefix + "/"`, shared by the request-summary middleware and the request-log level filter.
- IsSteppedUp was permanently true when BaseLevel == StepUpLevel (and inverted when the levels were), because state was inferred from a level comparison. It is now tracked by an explicit flag. PerformStepDown is also idempotent, closing a forced-cap + stale-timer double-step-down that drove the active-state counter negative.
- Options validation now rejects non-positive PreErrorBufferSize and PreErrorMaxContexts (previously clamped to 1 silently).
- The step-up trigger sink's background loop survives a throwing Trigger() instead of faulting and permanently disabling step-up.
- Request-body capture uses ReadBlock so a body larger than the reader buffer is captured in full rather than truncated by a short read.
- Pre-error buffer: get-or-create and enqueue are now atomic under the LRU gate, closing a race that could orphan a just-buffered event when a concurrent eviction removed its context.

## [3.2.1] - 2026-07-10

The v3.2.0 tag pointed at a commit whose csproj was still versioned 3.1.0, so no 3.2.0
package was published; 3.2.1 supersedes it.

### Fixed
- Pre-error buffer now floors buffered events at the resolved StepUpLevel, enforced at buffering time. Previously the buffer captured every event (including Verbose/Debug) regardless of configured levels, so an error retroactively exported events below the most detailed level the user opted into. Verbose/Debug events no longer flush on error unless StepUpLevel is set that low; set StepUpLevel to "Verbose" to restore the old firehose behaviour. NeverStepUpCategories remains unaffected — the buffer floor is by level only. Fixes #13. See docs/adr/0015-preerror-buffer-level-floor.md.

## [3.1.0] - 2026-07-10

### Added
- NeverStepUpCategories option: a list of Serilog SourceContext prefixes the step-up never raises above BaseLevel. Defaults to ["Microsoft.EntityFrameworkCore.Database.Command"]. Observable behaviour change: EF Core SQL commands (logged at Information) are no longer exported during the step-up window by default, sparing DB-backed services an SQL flood — and unredacted SQL export — for the duration of every incident. Warning/Error events in listed categories still export (a listed category is pinned to BaseLevel, not silenced); matching is ordinal on an exact or `prefix.`-delimited SourceContext; the list has no effect in AlwaysOn mode; the pre-error buffer is not filtered by it. Set "NeverStepUpCategories": [] to restore 3.0.0 behaviour. No public API break. See docs/adr/0021-never-step-up-categories.md.

## [3.0.0] - 2026-07-10

BREAKING. See MIGRATION.md for rationale and restoration steps.

### Changed (breaking)
- ClientIp no longer trusts X-Forwarded-For by default; it now comes from Connection.RemoteIpAddress. Opt back in with TrustForwardedHeaders: true.
- Request summaries carry a new redacted ForwardedFor property when X-Forwarded-For is present.
- MaxBodyCaptureBytes <= 0 now fails at startup with OptionsValidationException instead of silently disabling body capture.
- Request bodies are now captured for failing (5xx) requests even before step-up engages.
- jti and User-Agent now pass through RedactionRegexes.

### Added
- TrustForwardedHeaders option (default false).
- MaxContinuousStepUpSeconds option (default 0) and StepUpCooldownSeconds option (default 300).

### Fixed
- Request bodies are redacted before truncation, not after.
- Redaction patterns compile with RegexOptions.NonBacktracking.
- OTLP headers and resource attributes are percent-decoded.
- Request summaries are no longer double-exported.
- reloadOnChange is restored for config-declared sinks.
- One ApplyRedaction span per request instead of one per redacted value.

## [2.0.0] - 2026-07-07

BREAKING. See MIGRATION.md.

### Changed (breaking)
- The enableConsoleLogging parameter is removed from both AddStepUpLogging(...) overloads. Console output is now driven solely by StepUpLoggingOptions.EnableConsoleLogging.
- The configure delegate changed from Action<HostBuilderContext, IServiceProvider, LoggerConfiguration> to Action<IServiceProvider, LoggerConfiguration>. The synthesized fake HostBuilderContext (with null Configuration/HostingEnvironment) is gone, removing an NRE risk — resolve IConfiguration/IHostEnvironment from the IServiceProvider instead.
- Config-declared Serilog:WriteTo sinks now sit behind the step-up LevelSwitch instead of exporting everything at Verbose from the Verbose root. Use the immediate (IsImmediate) or request-summary (IsRequestSummary) bypass for always-on export.

### Fixed
- Removed the reflective DiagnosticContext registration.
- The bypass logger is disposed on shutdown so its async buffers flush.
- The File sink is shared.

## [1.14.0] - 2026-07-07

Audit fixes. No public API breaking changes.

### Security
- Redaction fails closed: a regex timeout yields [REDACTION-ERROR] instead of leaking the raw value.

### Fixed
- The step-down timer uses a generation counter and a monotonic clock, so a stale callback can no longer step down after a window extension.
- Request body capture works with endpoints that read the body; buffering is enabled before the pipeline runs.
- The request summary is emitted even when the handler throws (status 500).
- PreErrorBuffer LRU eviction is now O(1).
- Invalid level strings and a non-positive DurationSeconds fail fast via ValidateOnStart instead of silently falling back. The DurationSeconds default is a single canonical 180.

### Removed
- Dead CallStackHelper; per-sink meters are now static; the unused Serilog.Expressions dependency is dropped.

## [1.13.1] - 2026-06-21

### Fixed
- AdditionalSensitiveHeaders are now matched case-insensitively. Previously the comparer was lost when copying the built-in header set, so custom sensitive headers were only redacted when their casing exactly matched the incoming request header - leaking secret values otherwise.

## [1.13.0] - 2026-05-28

### Added
- Request log entries include a Jti property when the authenticated identity carries a jti claim, on both the AlwaysLogRequestSummary bypass path and the Serilog request logging enrichment path. EmitRequestSummary gains an optional jti parameter for callers that pass identity context directly.

## [1.12.1] - 2026-05-20

### Fixed
- Immediate log events (LogImmediate* / BeginImmediateScope) were written twice when an error triggered a pre-error buffer flush. PreErrorBufferSink now skips buffering events tagged IsImmediate=true, since ImmediateSink already forwarded them.

## [1.12.0] - 2026-05-20

### Added
- ImmediateLoggerExtensions with LogImmediateInformation, LogImmediateWarning, LogImmediateError, and BeginImmediateScope — extension methods on ILogger that bypass the step-up LevelSwitch and always export.
- ImmediateSink, and a new OpenTelemetry meter StepUpLogging.Immediate tracking immediate-routed events.

### Changed
- The step-up branch was refactored into a dedicated StepUpSink. Both it and ImmediateSink guarantee exactly-once delivery.

## [1.11.0] - 2026-04-30

### Added
- UserAgent and ClientIp fields on request summaries when AlwaysLogRequestSummary is enabled, including proxy-aware IP detection via the X-Forwarded-For header with a graceful fallback to the direct connection IP.

## [1.10.0] - 2026-03-09

### Added
- EnrichWithCallStack option.

## [1.9.0] - 2026-03-07

### Added
- Structured query string and route value logging.

## [1.8.1] - 2026-03-07

### Fixed
- Circular DI deadlock during AddStepUpLogging startup: Serilog.ILogger is no longer resolved while registering StepUpLoggingController.
- IsOpenTelemetryRegistered recognises the hosted service renamed in OpenTelemetry SDK 1.15.0.
- DiagnosticContext DI registration made robust.

## [1.8.0] - 2026-03-06

### Added
- AlwaysLogRequestSummary option to enable a guaranteed per-request Information-level summary.
- SummarySink to forward IsRequestSummary events to a DI-managed summary logger so summaries are exported independently of the step-up level.
- StepUpLoggingController.EmitRequestSummary API to emit structured request summaries (method, path, status code, elapsedMs, traceId).
- Unit and integration tests for the new summary behavior.

### Fixed
- Avoid duplicate unmanaged Serilog logger instances by centralizing the DI-managed summary logger.


*See README.md for configuration examples.*
