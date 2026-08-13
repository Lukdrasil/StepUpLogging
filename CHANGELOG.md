# Changelog

All notable changes to this project will be documented in this file.

Releases from 1.8.0 onward are recorded here. Entries backfilled from each release tag's
`PackageReleaseNotes` and, where those were not updated per release, from the commits between tags.
Tags before 1.8.0 predate this file.

## [Unreleased]

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
