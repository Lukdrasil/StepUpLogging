# Migrating to v4.0.0

v4.0.0 is a **breaking** release. It bundles four breaking changes to the audit logging contract
introduced in v3.5.0, plus three additive fields and a new opt-in sink. This guide takes one break
per section, with the exact code needed for each. Breaks 1, 2 and 4 stop the build at your call
sites; break 3 compiles, and instead throws where the registration call itself runs — not at host
start, which is where the unrelated missing-`AddStepUpLogging` prerequisite surfaces. Break 4 also
has a corner the compiler waves through — read it even if your build is already green. If you have
not adopted audit logging, none of this applies to you.

## 1. `ActorType` is now `required`

**What changed.** `AuditEvent.ActorType` defaulted to `"user"` when omitted. It is now `required`,
so every object initializer that builds an `AuditEvent` must set it.

**Why.** A default of `"user"` on an append-only record is a permanent, plausible-looking lie about
who acted whenever the actor was actually a service, an API key, or anonymous. Making it required
forces every call site to state the real actor kind once, at the only place that knows it.

**Migrate.** Supply the actor kind the call site actually knows — `"service"`, `"api-key"`,
`"anonymous"` — rather than pasting `"user"` everywhere, which would carry the old lie forward
under a new spelling:

```csharp
// Before (v3.5.0)
var evt = new AuditEvent
{
    Action = "order.cancel",
    ActorId = actorId,
    Outcome = AuditOutcome.Success
};

// After (v4.0.0)
var evt = new AuditEvent
{
    Action = "order.cancel",
    ActorId = actorId,
    ActorType = "service",
    Outcome = AuditOutcome.Success
};
```

A `with` expression over an event a factory already built needs no change — the factory sets
`ActorType`.

## 2. `Success`/`Failure`/`Denied` take `actorType` as a third parameter

**What changed.** The two-argument factories are gone; the signature is
`Success(action, actorId, actorType)`, and the same for `Failure` and `Denied`.

**Why.** Forced by break 1: with `ActorType` required, a two-argument factory could not compile
inside the library itself. It also puts the actor kind where the compiler can demand it, at the
shortest path to building an event.

**Migrate:**

```csharp
// Before (v3.5.0)
var evt = AuditEvent.Success("order.cancel", actorId);

// After (v4.0.0)
var evt = AuditEvent.Success("order.cancel", actorId, "user");
```

Read the argument order back at every migrated site — `action, actorId, actorType` is three
strings in a row, and nothing but that order stops two of them being transposed. The compiler
cannot catch a transposition, and the resulting record is permanent.

## 3. `AddAuditLogging` throws on a second sink registration

**What changed.** Calling `AddAuditLogging<TSink>()` while an `IAuditEventSink` is already
registered ahead of it — by an earlier `AddAuditLogging`, or by a direct
`services.AddSingleton<IAuditEventSink, …>()` — now throws an `InvalidOperationException`, at the
point the second registration call runs, naming the sink being added and, where known, the one
already registered. Previously the second registration silently won, and the first sink was never
audited through. The guard only sees registrations that ran before it: a direct
`AddSingleton<IAuditEventSink, …>()` called *after* `AddAuditLogging` still silently wins today,
unchanged from v3.5.0.

**Why.** A consumer following the README's own example and also calling a package's registration
method (e.g. `AddEncryptedSpoolAuditSink`) would lose one sink without a word.

**Migrate.** Register a sink exactly once. Where a test host substitutes a double for the sink
`Program.cs` already registered, drop the existing registration first:

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions; // RemoveAll

// Before (v3.5.0)
builder.AddAuditLogging<RecordingAuditSink>(ServiceLifetime.Singleton);

// After (v4.0.0)
builder.Services.RemoveAll<IAuditEventSink>();
builder.AddAuditLogging<RecordingAuditSink>(ServiceLifetime.Singleton);
```

From `ConfigureTestServices`, where only the `IServiceCollection` is in reach, do the same with
`services.RemoveAll<IAuditEventSink>()` and then register the double directly — `AddAuditLogging`
is an `IHostApplicationBuilder` extension and its other registrations already ran in `Program.cs`.

## 4. `IAuditEventSink.WriteAsync` returns `AuditWriteResult`

**What changed.** `WriteAsync` returned a bare `ValueTask`; it now returns
`ValueTask<AuditWriteResult>`, an enum with `Stored` and `Dropped` (no zero member).

**Why.** A sink that deliberately discards a record under back-pressure had no way to say so: the
old contract could only express "stored" (normal return) or "failed" (exception), so a discarded
record was still counted in `audit_events_total` and still wrote a companion log — the
`rate(audit_events_total[1h]) == 0` alarm read healthy exactly while audit was being lost.

**Migrate.** End every `WriteAsync` implementation with an explicit result. This is the one break
the compiler only half-guides you through: the signature change is a compile error, but a body
ending in `return default;` compiles straight through it. `AuditWriteResult` has no zero member by
design, so that `default` throws `InvalidOperationException` naming your sink type at the first
audit write — check every implementation by eye:

```csharp
// Before (v3.5.0)
public ValueTask WriteAsync(AuditEvent auditEvent)
{
    _store.Insert(auditEvent);
    return ValueTask.CompletedTask;
}

// After (v4.0.0)
public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
{
    _store.Insert(auditEvent);
    return ValueTask.FromResult(AuditWriteResult.Stored);
}
```

If your sink intentionally discards a record (e.g. a full write-ahead spool), return
`AuditWriteResult.Dropped` instead — this skips the companion log and moves
`audit_events_dropped_total` rather than `audit_events_total`, keeping both counters honest.

## Non-breaking additions in v4.0.0

- **`OldValues`/`NewValues`** on `AuditEvent` (`IReadOnlyDictionary<string, object?>?`). Optional,
  caller-supplied, and — like `Data` — never redacted.
- **`EventId`** on `AuditEvent`, owned by the library: a UUIDv7 (`Guid.CreateVersion7()`) is
  stamped on every `AuditAsync` call, alongside `TimestampUtc`/`TraceId`/`SpanId`, and a value set
  through a `with` expression is overwritten before the record reaches your sink. A retrying or
  spooling sink can deliver a record more than once; `EventId` is what lets the receiver
  deduplicate. No source change is required, but every record now carries an identifier your sink
  and its store did not see before; take it as the deduplication key. Caller-controlled
  idempotency (a retried business operation deliberately reusing one `EventId`) is not
  expressible, by design: it would let a receiver silently discard a repeated record as a
  duplicate, which is the same silent audit loss the library exists to prevent.
- **`audit_events_dropped_total`** counter under the `StepUpLogging.Audit` meter. It moves only
  when a sink returns `AuditWriteResult.Dropped` (break 4); `rate(audit_events_dropped_total[1h])
  > 0` is an alarm in its own right, separate from the existing `audit_events_total`/
  `audit_write_failures_total` pair.
- **`EncryptedSpoolAuditSink`**, an opt-in sink registered via `AddEncryptedSpoolAuditSink`. It
  spools audit records to disk write-ahead and drains them to a configured endpoint, encrypting
  each payload through an `IAuditPayloadEncryptor` port your application implements and supplies.
  Nothing changes if you do not call it.

---

# Migrating to v3.0.0

v3.0.0 is a **breaking** release. It bundles five breaking behavior changes plus several
non-breaking security and correctness fixes. This guide covers each break with the exact
config/code needed to restore the old behavior (or why you should not). If you are already
on v2.x, read this section first, then the v2.0.0 section below only if upgrading from v1.

## 1. `ClientIp` no longer trusts `X-Forwarded-For`

**What changed.** In v2, `ClientIp` on a request summary was taken from the first entry of
the `X-Forwarded-For` (XFF) header, falling back to the direct connection address. In v3,
`ClientIp` comes from `HttpContext.Connection.RemoteIpAddress` by default and **ignores XFF**.

**Why.** `X-Forwarded-For` is client-supplied and trivially spoofable. Trusting it
unconditionally lets any caller forge the logged client IP. Correctly resolving the real
client address behind a proxy is the job of ASP.NET Core's `ForwardedHeadersMiddleware`,
which validates against known proxies — the library cannot guess your proxy topology.

**Restore v2 behavior (only behind a proxy you control):**

```json
{
  "SerilogStepUp": {
    "TrustForwardedHeaders": true
  }
}
```

Prefer the correct fix instead: configure `ForwardedHeadersMiddleware` so
`Connection.RemoteIpAddress` already reflects the real client, and leave
`TrustForwardedHeaders` at its default `false`:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor,
    // Restrict to your proxies — do NOT leave KnownNetworks/KnownProxies empty in production.
});
```

## 2. New `ForwardedFor` field on request summaries

**What changed.** When the `X-Forwarded-For` header is present, request summaries now carry
an additional `ForwardedFor` property (redacted through `RedactionRegexes`). It is absent
when the header is not present. This is independent of `TrustForwardedHeaders`: you always
see the raw forwarded chain (redacted) for diagnostics, separately from the trusted
`ClientIp`.

**Action.** None required. If you have downstream log schemas or dashboards that reject
unknown fields, add `ForwardedFor` (string, optional) to them.

## 3. `MaxBodyCaptureBytes <= 0` now fails at startup

**What changed.** In v2 a non-positive `MaxBodyCaptureBytes` silently disabled body capture
and logged `[UNAVAILABLE]` for every request body. In v3 it fails fast at startup via
`ValidateOnStart`, throwing `OptionsValidationException`.

**Why.** A misconfigured cap that silently drops all bodies is a debugging trap. Failing at
startup surfaces the mistake immediately.

**Action.** If you set `MaxBodyCaptureBytes` to `0` (or negative) to turn body capture off,
use the dedicated switch instead:

```json
{
  "SerilogStepUp": {
    "CaptureRequestBody": false
  }
}
```

## 4. Request bodies captured for failing (5xx) requests before step-up engages

**What changed.** In v2, a request body was captured only while step-up was already active.
The request that itself *triggered* step-up raced the async trigger channel and usually
logged no body. In v3, the body is also captured when the response status is `>= 500`, even
if step-up has not engaged yet.

**Why.** The request that caused the error is the most valuable one to inspect, and it was
precisely the one whose body went missing.

**Action.** None required — but expect captured bodies on 5xx requests that previously logged
none. Redaction still applies. If body volume is a concern, tune `MaxBodyCaptureBytes` or
disable capture.

## 5. `jti` and `User-Agent` now pass through redaction

**What changed.** The JWT `jti` claim and the `User-Agent` value on request summaries are now
run through `RedactionRegexes` like every other request-derived field.

**Why.** Consistency — no user-supplied value should bypass redaction (see CLAUDE.md).

**Action.** None required unless one of your redaction patterns matches a legitimate
`User-Agent`/`jti` substring, in which case that field will show `[REDACTED]`. Narrow the
pattern if so.

## Non-breaking fixes in v3.0.0

You do not need to change anything for these, but they change observable behavior:

- **Body redaction before truncation.** Request bodies are redacted *before* being truncated
  to `MaxBodyCaptureBytes`, so a secret straddling the byte boundary can no longer leak its
  prefix.
- **Linear-time redaction.** Patterns compile with `RegexOptions.NonBacktracking` (guaranteed
  linear time), falling back to `Compiled` only for lookaround/backreference patterns. The
  100 ms timeout and the fail-closed `[REDACTION-ERROR]` sentinel are unchanged.
- **OTLP env-var decoding.** `OTEL_EXPORTER_OTLP_HEADERS` and `OTEL_RESOURCE_ATTRIBUTES`
  values are percent-decoded per the OTel spec, so `Authorization=Basic%20...` reaches the
  collector intact.
- **No double-export of summaries.** Request-summary events are no longer duplicated through
  the pre-error buffer flush.
- **`reloadOnChange` restored.** `Serilog:MinimumLevel:Override` honours `reloadOnChange`
  again — the config split now wraps the live `IConfiguration` instead of snapshotting it.
- **One redaction span per request** instead of one `ApplyRedaction` activity per redacted
  field.

## New options in v3.0.0

All three are backward compatible (their defaults preserve v2 semantics except where a break
above applies):

| Option | Default | Description |
|---|---|---|
| `TrustForwardedHeaders` | `false` | When `true`, `ClientIp` is taken from the first `X-Forwarded-For` entry (v2 behavior). Only enable behind a proxy you control with `ForwardedHeadersMiddleware`. |
| `MaxContinuousStepUpSeconds` | `0` (disabled) | Upper bound, in seconds, on a single continuous step-up window. When exceeded the level is forced back to `BaseLevel` and a cooldown opens. `0` disables the cap. Must be `0` or `>= DurationSeconds`. |
| `StepUpCooldownSeconds` | `300` | Seconds during which triggers are ignored after the cap forces a step-down. Ignored when the cap is disabled. |

**Bounding sustained step-up cost:**

```json
{
  "SerilogStepUp": {
    "MaxContinuousStepUpSeconds": 900,
    "StepUpCooldownSeconds": 300
  }
}
```

This forces a step-down after 15 continuous minutes of elevated logging and then ignores new
triggers for 5 minutes. Note the security caveat (ADR 0010): an attacker pacing triggers
outside the cooldown still obtains sustained verbosity at a reduced duty cycle —
collector-side sampling/quota remains the backstop.

---

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
