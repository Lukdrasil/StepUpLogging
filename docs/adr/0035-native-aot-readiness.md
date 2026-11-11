# ADR 0035 - Native AOT readiness

- Status: Accepted
- Date: 2026-11-11

## Context

Published with `PublishAot=true`, a consumer of the library aborts at startup on any `Serilog` section without
`Serilog:Using`: `ReadFrom.Configuration` scans for sink assemblies and a native image has nothing to scan. The
encrypted spool serializes with reflection-based System.Text.Json, which a native image switches off.

## Decision

**D1. Explicit reader assemblies only in a native image.** When `RuntimeFeature.IsDynamicCodeSupported` is false,
every `ReadFrom.Configuration` call gets `ConfigurationReaderOptions` with the Serilog assemblies the library
references plus each non-blank `Serilog:Using` entry loaded by name; the explicit reader ignores `Using` on its own.
Under JIT the call is unchanged, so a sink found by scanning without `Using` keeps working.

**D2. Two source-generated spool contexts.** The envelope context has no naming policy and the payload context uses
camelCase, mirroring the two option sets they replace; the wire bytes are unchanged.

**D3. Payload reflection fallback where reflection JSON is on.** `AuditEvent.Data`, `OldValues` and `NewValues`
hold `object?` values. The payload context declares common value types; while
`JsonSerializer.IsReflectionEnabledByDefault` is true a reflection resolver follows it, so a JIT caller's own types
serialize as before. In a native image an undeclared value type throws `NotSupportedException`; no public resolver
hook is added.

**D4. Configuration binding generator** replaces the reflection binder for `SerilogStepUp`.

## Consequences

JIT and native behaviour differ for config-only sinks not named in `Using` and for undeclared `Data` value types.
An AOT publish-and-run consumer test guards the native path.
