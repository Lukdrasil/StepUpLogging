# ADR 0022 — Opt-in redaction of application log event properties

- Status: Accepted
- Date: 2026-08-13
- Issue: #20

## Context

`CompiledRedactionPatterns.Redact()` is applied in 12 places today — 11 in the request-logging
middleware (query string, route values, header values, User-Agent, `jti`, request body,
`X-Forwarded-For`) and one in `AuditLogger.cs:127`. Ordinary application log events — anything a
consumer writes through `ILogger`, `LogImmediate*`, or a `[LoggerMessage]` method — are not
redacted in any version. A consumer who has seen redaction work on query strings can reasonably
assume it works everywhere; it does not.

Three facts about the existing pipeline constrain the answer:

- `StepUpLoggingOptions.RedactionRegexes` defaults to `[]`, and `Redact` returns its input
  verbatim when there are no patterns. Cost for an unconfigured consumer is one array check.
- Serilog's `LogEvent` exposes `MessageTemplate` and `Exception` without setters. An enricher can
  reach `Properties` and nothing else.
- `ApplyCommonEnrichers` (`StepUpLoggingExtensions.cs:390-437`) is applied to both the root logger
  and the bypass logger, and `PreErrorBufferSink` re-emits the *same* `LogEvent` reference — so an
  enricher registered there runs twice on buffered events.

## Decision

1. **A new internal sealed `ILogEventEnricher` redacts string-valued scalar properties only.**
   Each `LogEventPropertyValue` that is a `ScalarValue` wrapping a `string` is passed through
   `CompiledRedactionPatterns.Redact()`; a changed value is written back with
   `AddOrUpdateProperty`. Non-string scalars, and structured, sequence and dictionary values, are
   left untouched. Audit records written through `IAuditEventSink` are out of scope (issue #20
   non-goal) and `AuditLogger.cs:127` is not modified.

2. **Message template text and exception messages are out of scope, and this is a documented
   limit.** `LogInformation("token={Token}", t)` and `[LoggerMessage]` produce properties and are
   covered. Interpolated `LogInformation($"token={t}")` bakes the secret into the template's
   literal text and produces no properties — unreachable from an enricher. Covering it would mean
   a sink wrapper that rebuilds each `LogEvent` around a re-parsed template, which makes the
   template per-event and destroys the structured-logging identity that OTLP and log backends
   group on. Rejected: the cure is worse than the disease, and the pattern it covers is already
   flagged by analyzers.

3. **Opt-in through a new `bool RedactLogEventProperties`, default `false`.** `RedactionRegexes`
   keeps its current meaning — HTTP input only — until the flag is set. Rejected: treating a
   non-empty `RedactionRegexes` as the switch. This library ships as a versioned NuGet package;
   a consumer redacting `\d{16}` in query strings would, on a patch upgrade, silently have every
   16-digit order ID in every log message replaced with `[REDACTED]` and would discover it in
   production.

4. **Registered on the root logger configuration only, not in `ApplyCommonEnrichers`.** Root
   enrichment precedes every sink, so `PreErrorBufferSink` buffers already-redacted events and no
   second pass is needed. A repo-wide sweep confirms exactly two writers reach the bypass logger
   without root enrichment: the startup-ordering warning (`StepUpLoggingExtensions.cs:245-247`),
   which carries only `LogEventLevel` values, and `StepUpLoggingController.EmitRequestSummary`
   (`:133-181`). Neither carries a **consumer-authored log event**, which is what issue #20 is
   about. Rejected: adding it to `ApplyCommonEnrichers`, which is a shorter diff but sweeps every
   buffered event twice on the error-flush path — the path already under the most load.

   Known residue, deliberately out of scope: the request summary's `{Path}` is only
   `TrimEnd('/')`-normalised (`StepUpLoggingExtensions.cs:522-523`) and emitted unredacted at
   `:552`, while the route values derived from the same path *are* redacted at `:539`. That is an
   inconsistency in the request-input redaction tier (ADR 0008/0009), not in this feature, and
   fixing it here would change existing output for every consumer with patterns set — the silent
   upgrade break D3 exists to prevent.

5. **The enricher is registered last — after the consumer hook**
   `configure?.Invoke(services, lc)` (`StepUpLoggingExtensions.cs:287`), not at the library's own
   enricher block. A redaction sweep that runs before other enrichers can still add properties is
   a sweep with a hole in it: a consumer who registers their own enricher and opts into redaction
   would reasonably expect their properties covered, and a flag that reads as on while silently
   skipping them is worse than no flag. Config-declared `Serilog:Enrich` (`:228`) and the library's
   own enrichers are both in place by that point. Rejected: registering immediately after
   `ApplyCommonEnrichers`, which is more predictably scoped but leaves everything stamped at `:287`
   unredacted. Consequence accepted: a consumer wanting an enricher exempt from redaction has no
   escape hatch short of turning the flag off.

   The dependency is passed **by constructor**, not resolved:
   `lc.Enrich.With(new RedactionEnricher(services.GetRequiredService<CompiledRedactionPatterns>()))`.
   The only enricher precedent in the repo is `Enrich.With<ActivityContextEnricher>()` (`:395`),
   whose generic form requires a parameterless constructor and would push an implementer toward a
   static field or a service-locator call inside `Enrich` — both barred by the standing
   no-reflection / no-unnecessary-abstraction directive. `services` is in scope at the registration
   point, so the instance form is available and is the one to use. The registration also
   short-circuits when there are no patterns to apply (`CompiledRedactionPatterns.Patterns.Length
   == 0`), so a consumer who sets the flag without configuring any pattern pays no per-event cost.

6. **A fixed exclusion set protects the library's own properties.** Never redacted:
   `TraceId`, `SpanId`, `ParentSpanId`, `TraceFlags`, `TraceState`, `SourceContext`, `Application`,
   `Environment`, `MachineName`, `ServiceVersion`, `ServiceInstanceId`, and `CallStack` — twelve
   names, not the eleven originally scoped. The set is maintained **by name, not by origin**: the
   twelve are stamped from six different places and no single registration site lists them.
   `Enrich.WithProperty` literals in `ApplyCommonEnrichers` (`StepUpLoggingExtensions.cs:390-437`)
   account for four — `Application` (`:396`), `Environment` (`:405`), `ServiceVersion` (`:430`),
   `ServiceInstanceId` (`:435`). `TraceId`/`SpanId` come from the third-party
   `Serilog.Enrichers.OpenTelemetry` (`:393-394`); `ParentSpanId`/`TraceFlags`/`TraceState` from
   the library's own `ActivityContextEnricher` (`:395`), which names them internally; `MachineName`
   from `Serilog.Enrichers.Environment` via `Enrich.WithMachineName()` (`:420`); and `CallStack`
   from `Serilog.Enrichers.CallStack` via `Enrich.WithCallStack()` (`:425`, gated by
   `EnrichWithCallStack`, default `false`). `SourceContext` is stamped by no enricher here at all —
   Serilog itself and the `ILogger<T>` category bridge produce it.

   That spread is the trap. `CallStack` was missed not because its name is unreadable — `MachineName`
   is equally a package's fact and was scoped from the start — but because `EnrichWithCallStack`
   defaults to `false`, so it appears on no default event and in no default log dump. Neither
   derivation is sufficient alone: the registration block (`:390-437`) omits `SourceContext`, and
   a sample event omits every opt-in enricher. The set must be reviewed against both whenever an
   enricher is added.

   `CallStack` belongs in the set for the same reason as the rest: it is a library-owned diagnostic
   value, never a consumer secret, and redacting it would leave a captured stack trace unreadable
   while protecting nothing. None of the twelve can carry a consumer secret, and two fail
   destructively if mangled: `ServiceInstanceId` is a GUID in "N" form, so it matches
   `[0-9a-f]{32}` — precisely the API-key pattern this decision is argued from — and silently
   splits OTLP resource identity; `SourceContext` is what
   `StepUpSink.Emit` matches `NeverStepUpCategories` against (ADR 0021), so redacting it would
   silently disable the EF Core deny-list rather than merely look wrong. `ThreadId` and `ProcessId`
   need no entry — they are non-string scalars and the enricher never touches them. Rejected:
   making the set configurable — a third knob for one feature, tuned by nobody until they have been
   bitten.

7. **No benchmark project and no new metric.** The flag defaults off and the empty-patterns
   short-circuit means zero regex work unless a consumer deliberately opts in; the existing 100 ms
   per-pattern regex timeout (`StepUpLoggingExtensions.cs:378`, `:382`) bounds the worst case for
   one who does. What that consumer pays is described rather than measured, because a measurement
   taken against the library's own sample regexes does not transfer to a consumer's — and
   describing it means naming its *shape*, which is the part that surprises when the flag goes on
   (never on upgrade: D3 keeps the flag off). D4 puts the enricher on the root configuration, whose
   minimum level is `MinimumLevel.Verbose()` (`StepUpLoggingExtensions.cs:229`) so that the buffer
   and trigger sinks see everything. The sweep therefore runs on every event that **reaches the
   root** — everything emitted bar what a config `MinimumLevel:Override` already filters — not on
   the subset exported: a Debug event that `StepUpSink.Emit` drops against the `LevelSwitch`
   (`StepUpSink.cs:43`) is swept before it is dropped. Per event that is one `Regex.Replace` per
   pattern (`StepUpLoggingExtensions.cs:977-981`) per non-excluded string-valued scalar property. This
   is inherent to the coverage D4 and D5 guarantee, not a defect to tune away, so it is documented
   on both surfaces a consumer reads before flipping the flag — the README's redaction section and
   the `RedactLogEventProperties` XML doc that ships as IntelliSense — along with the mitigation
   that exists: few patterns, each narrow rather than open-ended. Anchoring is deliberately not
   advised: patterns compile `NonBacktracking` where supported (`:378`), so anchoring saves start
   positions rather than preventing blowup, and a pattern anchored with `^`/`$` would stop matching
   the mid-value secrets this feature exists to catch. No redaction-hit counter is added to the
   existing meters.

## Consequences

- The gap the issue describes closes only for consumers who both set `RedactionRegexes` and flip
  `RedactLogEventProperties`. Off by default means most consumers stay exactly where they are —
  deliberate, given the alternative is a silent behaviour change on upgrade.
- Redaction is now a two-tier concept: request-input redaction (always on when patterns are set)
  and application-log redaction (opt-in). Four surfaces stated the limit **absolutely** and became
  wrong — not merely incomplete — the moment D1 shipped. The three live surfaces were rewritten
  rather than given a trailing caveat; ADR 0021, being a decision record, keeps its original text
  and carries an `Amended by ADR 0022` note in the house style ADR 0016 established:
  1. the **shipped XML doc** on `StepUpLoggingOptions.RedactionRegexes`, which named the exact
     `logger.LogInformation("token={T}", secret)` case D1 covers. This was the highest-priority
     one: it ships inside the NuGet package as IntelliSense, is an API contract rather than prose,
     and cannot be corrected in releases already published.
  2. `README.md` (Security section, identical example), plus the feature bullet, the options
     table, and the `NeverStepUpCategories` prose.
  3. `StepUpLoggingOptions.NeverStepUpCategories`'s remark ("carry unredacted SQL").
  4. ADR 0021, which cited the options XML doc as the canonical statement of the limit and became
     conditionally false once the flag is on.
- `[REDACTION-ERROR]` can now replace an application log value, not just a request field. This
  reuses the existing fail-closed behaviour rather than introducing a second error convention. With
  more than one pattern configured, a later pattern can rewrite part of an earlier
  `[REDACTION-ERROR]` sentinel; the original value is never leaked either way, only the exact
  sentinel text is not always guaranteed verbatim.
- Structured values remain a hole: a secret inside `{@user}` is not redacted. Recursion is
  additive and can be added later without a breaking change.
