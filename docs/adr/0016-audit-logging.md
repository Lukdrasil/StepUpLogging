# ADR 0016 — Audit Logging

- Status: Accepted
- Date: 2026-08-05

## Context

Audit trails are a compliance requirement for many applications — they answer "who did what, to what, and with what outcome?" in response to regulatory investigations and security incidents. The library controls log volume in both directions: downward via step-up gating (slowing the logs when normal), and upward to ensure certain events are never lost (the audit record). Without audit logging, the library's step-up gating could inadvertently drop events that must be kept.

The decision to admit audit logging here is conditional on a boundary: the library remains "controls log volume — down via step-up, up for what must not be lost" and ships no retention policy, hash chaining, or query API. The moment audit grows any of those, it becomes a separate package. Without this boundary, every future request has no line to be measured against.

## Decision

1. **Audit writes bypass Serilog entirely.** Call sites invoke `IAuditLogger<T>.AuditAsync()`, which calls `IAuditEventSink.WriteAsync()` directly. Serilog is not involved.
   - *Rationale:* `ILogEventSink.Emit(LogEvent)` has a `void` return type: no `await`, no exception propagation, no shared `DbContext` (singleton scope), and typed records degrade to untyped `ScalarValue` bags. This is a hard limit of the Serilog interface, not a quality-of-implementation issue. Audit records are not routine logs; they warrant a contract that allows awaiting, exception propagation, and scoped transactions.

2. **Sink exceptions propagate to the call site unchanged.** No `FailureMode` configuration option, no silent retry, no "log and continue."
   - *Rationale:* Silent failure is worse than no audit. An audit trail that silently stops working (configured once, forgotten, audits missing for months) is worse than one never wired. A consumer who wants continue-on-failure writes `try/catch` in their own sink, where it is visible in review and tested explicitly. The library observes failures only long enough to count them, then rethrows. There is no retry or backoff; that belongs in the sink.

3. **The package ships no `IAuditEventSink` implementation.** No default, no no-op, no test double, no sample in the package.
   - *Rationale:* A default mirror-to-log sink manufactures false confidence: audit "works", records appear in OTLP, and a year later the trail turns out to have seven-day retention in a backend with weak ACLs. That is worse than having no audit at all, because it was relied upon. A no-op sink silently swallows records, which is the same trap. Shipping a test double defeats the point of D18 (consumers write their own, suited to their needs). A code snippet in the README is sufficient.

4. **Disabling audit means not calling `AddAuditLogging`.** There is no `Enabled` configuration flag.
   - *Rationale:* An `Audit:Enabled: false` setting leaves `IAuditLogger` injectable and silently no-op — the same false confidence as D10. Auditing is a compliance decision that belongs in code, not configuration: an ordinary `if` in `Program.cs` for environment-conditional auditing. Developers and reviewers see the decision explicitly; it is never buried in appsettings.

5. **Redaction follows the origin of the value, not the data type.**
   - *Rationale:* What the *caller* supplies (`ActorId`, `TargetType`, `TargetId`, `Action`, `Reason`, `Data`) is never redacted — the sink owns that policy, it knows its store's ACL. What the *library* reads out of the request (`UserAgent`, and forwarded IPs if trusted) goes through `Redact()` exactly as in the request-logging middleware.
   - **Asymmetry detail:** `SourceIp` from `Connection.RemoteIpAddress` is not redacted because it is not client-supplied — it comes from the network layer and cannot be forged. `SourceIp` from `X-Forwarded-For` (when `TrustForwardedHeaders = true`) is redacted, because that header is client-supplied and matches the `ExtractClientAddresses` policy. This split keeps the CLAUDE.md rule *"never bypass redaction for user-supplied values"* intact in its original sense (user-supplied = out of the HTTP request, not authored by the developer), so this ADR clarifies where the boundary lies instead of carving an exception out of it.
   - *Note:* A global redaction enricher for all log events is a legitimate but separate feature (filed as GitHub issue #20).

6. **No `CancellationToken` parameter on `IAuditEventSink.WriteAsync` or `IAuditLogger.AuditAsync`.**
   - *Rationale:* Call sites reflexively pass `HttpContext.RequestAborted` as a token. If a client disconnects mid-write and the token is cancelled, the `OperationCanceledException` surfaces at the business code, ASP.NET Core swallows it as an ordinary abort, and the audit record vanishes silently. This is catastrophic at `Outcome.Denied` — an attacker could erase their own trace by dropping the connection. A signature that invites misuse is worse than a missing parameter. Bounding a slow sink is the sink's own responsibility: `await sink.WriteAsync(e).AsTask().WaitAsync(timeout)` is the escape hatch (never use `.Result`).

7. **Order: audit write first, companion log second.** The companion log is only invoked after the write succeeds.
   - *Rationale:* Audit-then-log preserves the invariant: "a companion log exists ⇒ an audit record exists." The converse (log-then-audit) would produce an immediate log asserting an action for which no audit record yet exists — and, since sink exceptions propagate, for which the business operation is aborted. The audit timestamp is stamped at entry, before the write, so sink latency does not affect it.

8. **`AddAuditLogging<TSink>` requires `AddStepUpLogging`.**
   - *Rationale:* Client IP is derived by the **existing** `ExtractClientAddresses` policy (ADR 0008), honouring `StepUpLoggingOptions.TrustForwardedHeaders`. The dependency is enforced structurally: the `AuditLogger<T>` implementation takes `CompiledRedactionPatterns` as a constructor dependency, and only `AddStepUpLogging` registers it. An app that forgot it fails when the first `IAuditLogger<T>` is resolved (not at startup, but when DI resolution first occurs), with a clear message naming `CompiledRedactionPatterns`. This is loud and never silent, but it is **not** `ValidateOnStart` parity with ADR 0007 — the check happens at first resolution, not at host startup.
   - *Alternative rejected:* Audit could derive client IP on its own rule. This would write a second, weaker client-IP rule, violating ADR 0008's mandate — "the package contains exactly one client-IP rule." Behind a reverse proxy with `TrustForwardedHeaders = true`, the request log would show real client IPs while every audit record carried the proxy's address, making the audit trail weaker than the diagnostic log it is supposed to outrank.

## Consequences

- Audit records are written durably (or fail audibly) without flowing through Serilog, removing a class of silent failures but requiring consumers to own the sink implementation.
- Exceptions during audit write propagate to the business call site, so audit failures are never hidden from error handling. Consumers retain full control over failure handling via their sink's `try/catch`.
- The library provides neither a default sink nor a configuration flag for disabling audit; these omissions prevent false confidence and make compliance decisions explicit in code.
- The redaction boundary is the *origin* of the value (library-supplied, client-supplied, or caller-supplied), not its data type. This clarifies the CLAUDE.md rule rather than carving an exception into it.
- No `CancellationToken` closes a semantic trap: a caller reflexively passing `RequestAborted` would erase audit on disconnect, which is worse than being prevented from trying.
- The strict ordering (audit, then log) makes the invariant "a companion log exists ⇒ an audit record exists" the simplest path, rather than a special case. The converse does not hold and is not intended to: the one-argument overload writes an audit record with no log at all.
- The hard requirement for `AddStepUpLogging` (via `CompiledRedactionPatterns` dependency) prevents audit from deriving its own client-IP rule, keeping ADR 0008's single-rule boundary intact.

### Metrics

The library emits two counters under the meter `StepUpLogging.Audit`:
- `audit_events_total{outcome}` — number of records written, tagged by outcome (Success, Failure, Denied, Unknown). Cardinality is 3 (or 4 with edge cases); never a free string.
- `audit_write_failures_total` — number of writes for which the sink's `WriteAsync` threw. The exception is counted and rethrown; the record did not reach the consumer's store.

Zero audit events over an observation window is itself an alarm that audit stopped working — alert on the rate of `audit_events_total`.
