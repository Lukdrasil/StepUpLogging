# ADR 0017 — The encrypted spooling audit sink ships inside the core package, behind an encryption port

- Status: Accepted
- Date: 2026-08-13 (rewritten at PLAN GATE v2 — originally proposed as a separate package)
- Task: T260812h-encrypted-spool · Issue #22

## Context

Issue #22 proposed a separate package `Lukdrasil.StepUpLogging.Audit.EncryptedSpool`, quoting
ADR 0016's boundary: *"The moment audit grows any of those [retry, spooling, health checks], it
becomes a separate package."* That boundary was drawn when the sink was expected to carry
encryption — keys, algorithms, crypto dependencies — inside itself.

Two PLAN GATE adjustments changed the facts underneath it. First, encryption **including all key
handling** moved behind a port the client application implements; the sink carries no crypto code
and no dependency beyond the BCL. Second, with that gone, the sink shrank to a handful of files:
options, an envelope wrapper, a spool writer, a drain worker, a health check, one DI extension.

What a separate package would still buy: an independent release cadence, and core's API surface
staying free of spool types. What it costs, permanently: a second csproj with its own metadata and
`InternalsVisibleTo`, a second test project, a rewritten two-path `publish.yml` with distinct tag
prefixes, a compatibility matrix in the README, and a forced release ordering. The developer
weighed this at the gate and chose one package.

## Decision

1. **The sink ships inside `Lukdrasil.StepUpLogging` 4.0.0**, in the namespace
   `Lukdrasil.StepUpLogging.Audit.EncryptedSpool`. One package, one version, the existing `v*`
   tag flow untouched.

2. **This amends ADR 0016 twice more** (on top of ADR 0018's four amendments): its D3 — "the
   package ships no `IAuditEventSink` implementation" — and the boundary sentence in its Context.
   D3's rationale is preserved, not overruled: it forbade a **default** sink that manufactures
   false confidence and a no-op that swallows records. `EncryptedSpoolAuditSink` is neither — it
   is dead code until `AddEncryptedSpoolAuditSink` is called explicitly, and per ADR 0016 D4 the
   registration call *is* the switch. The consumer who writes their own sink is exactly as
   supported as before.

3. **The package depends on no encryption implementation.** It defines a narrow port —
   `IAuditPayloadEncryptor`: take the serialized payload plus its context, return an opaque
   encrypted blob — and the host registers its implementation in `Program.cs`. The package never
   sees a key, an algorithm or an envelope format, and it never acquires keys (advisory analysis
   preserved in the withdrawn ADR 0019). Its own responsibilities are exactly: serialize → hand to
   the port → spool to disk → deliver to the configured endpoint (URL and producer credentials in
   configuration) → delete after 2xx.

4. **The spool holds an opaque blob** wrapped in a thin outer object carrying `eventId` and
   `createdUtc` in clear, so the drain worker orders and deduplicates without decrypting —
   whatever envelope the encryptor produces cannot break the spool.

5. **`AddEncryptedSpoolAuditSink(options => …)` calls
   `AddAuditLogging<EncryptedSpoolAuditSink>(ServiceLifetime.Singleton)` internally**, so the
   consumer calls it plus `AddStepUpLogging` and never `AddAuditLogging` directly. The sink is a
   singleton because it owns the spool directory and an `HttpClient`. **No `Enabled` flag**, per
   ADR 0016 D4.

6. **Its instruments live on the existing `"StepUpLogging.Audit"` meter** — already exported by
   `AddStepUpLoggingMeters()` (`StepUpLoggingExtensions.cs:423-432`), so consumers' OTel wiring
   needs no change.

## Consequences

- The 4.0.0 release carries both the `AuditEvent` contract changes (ADR 0018) and the new sink;
  one tag publishes everything, and no compatibility matrix exists because there is nothing to
  cross-version.
- The packaging blocks of the original plan (project scaffold, two-path CI, second test project,
  release-order coordination) disappear; tests land in the existing test project, which already
  holds the `InternalsVisibleTo` grant.
- Any future **breaking** change to the spool's public surface (the options type, the port, the
  extension method) forces a major version of the whole package. This is the accepted price of
  merging, and the surface is deliberately kept small because of it.
- A consumer who never audits carries the sink's types in their assembly. They reference nothing,
  register nothing, and cost nothing at runtime.

## Rejected alternatives

- **A separate package `Lukdrasil.StepUpLogging.Audit.EncryptedSpool`** (the issue's original
  shape, and this ADR's own first draft). Honors ADR 0016's boundary literally and lets the sink
  iterate without touching core's version — but every gram of that flexibility is paid for in
  permanent CI, packaging and documentation overhead, and the boundary's original motivation
  (crypto growth inside the sink) no longer exists now that encryption sits behind a port.
- **A direct dependency on the client's encryption library.** Blocks this work on that library's
  release and propagates its breaking changes into ours; the port keeps the coupling at one
  interface.
- **Implementing the encryption inside this package.** Rejected twice over: it recreates the
  dependency problem the port removed, and the receiving side would have to reimplement the same
  format from a specification rather than sharing the client library's code.
