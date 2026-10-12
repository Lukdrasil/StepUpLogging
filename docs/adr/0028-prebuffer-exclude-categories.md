# ADR 0028 - Pre-error buffer exclude categories

- Status: Accepted
- Date: 2026-10-12

## Context

The pre-error buffer holds every held-back event at or above `StepUpLevel` (ADR 0015, ADR 0023),
framework categories included. An `Error` in a trace therefore exports the trace's Polly,
`System.Net.Http.HttpClient`, Routing, Hosting and Kestrel `Information` events too. A consumer
that wants those categories at `Warning` in `Auto` mode, even on an `Error` flush, had no way to say
so (issue #79, found in userorg #334):

- `NeverStepUpCategories` pins the export minimum only; its events are still buffered and flushed
  (ADR 0023 D4).
- A `CategoryFloors` floor rejects the event, which makes it a held-back event, so it is buffered too.
- A root `Serilog:MinimumLevel:Override` drops the event before any sink, so Diagnostic mode can no
  longer lift it.

## Decision

**D1. `PreErrorBufferExcludeCategories` (default `[]`) filters the buffer, not the export.**
`PreErrorBufferSink.Hold` drops an event whose `SourceContext` matches a listed prefix. Matching is
the shared `CategoryPrefix` rule (ADR 0021 D3). The export decision in `StepUpSink` is unchanged, so
an event the switch or a floor lets through is still exported. Diagnostic mode lifts the floors and
the switch sits at `DiagnosticLevel`, so a listed category's events at or above it (`Debug` by
default) are exported directly there. Filtering in `StepUpSink` before `Hold` was rejected: the rule
is about what the buffer keeps, and `Hold` already owns the other buffer filters.

**D2. Only events below `Warning` are dropped.** A `Warning` of a listed category is held when the
switch or a floor rejects it (for example `BaseLevel = Error`) and an `Error` flushes it, so the list
can never hide a warning that leads up to an error. `Error` and `Fatal` are never held anyway.

**D3. Blank entries are ignored at wiring**, like the sibling category lists. The option is not
validated. An empty list costs one level compare and one length check per held event, with no
allocation.

## Consequences

- With the list set, an `Error` flush exports only the events of other categories of the trace and
  the listed categories' `Warning` events.
- A listed category's held events below `Warning` are lost, by design; to see them, use Diagnostic
  mode or raise the switch.
- No public-surface change besides the new option: `PreErrorBufferSink` gains an optional internal
  constructor parameter.
